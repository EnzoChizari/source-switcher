namespace WindowsSourceSwitcher.Core;

public enum RateProvenance
{
    Unknown,
    WindowsMixFormat,
    ApplicationReported,
    CatalogMetadata,
    VerifiedNative,
    WasapiStream,
    BrowserDecoder,
    PlayerMetadata
}

/// <summary>
/// VerifiedNative must mean independently verified source/content rate. An endpoint mix format,
/// capture format, media-session playback status, or guessed application default never qualifies.
/// </summary>
public sealed record RateObservation(
    string SourceKey,
    string SessionToken,
    long SelectionRevision,
    int? SampleRateHz,
    RateProvenance Provenance,
    string Detail,
    int? BitsPerSample = null,
    bool UsesFloatPcm = false);

public enum RateDecisionReason
{
    SwitchingDisabled,
    NoMasterSelected,
    ManualOverride,
    VerifiedNative,
    CatalogMetadata,
    SourcePaused,
    SourceUnavailable,
    NativeRateUnknown,
    ConflictingObservations,
    WasapiStream,
    BrowserDecoder,
    BrowserHold,
    PlayerMetadata
}

public sealed record RateDecision(int? DesiredSampleRateHz, RateDecisionReason Reason, string Detail,
    int? DesiredBitsPerSample = null, bool ManualBitDepth = false);

/// <summary>Pure selection policy: endpoint calls and remembering successful changes belong to the host.</summary>
public static class SampleRatePolicy
{
    public static RateDecision Evaluate(
        SourceSettings settings,
        IReadOnlyList<SourceSnapshot> sources,
        IEnumerable<RateObservation>? observations = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(sources);
        settings.Validate();
        if (!settings.SwitchingEnabled)
            return new(null, RateDecisionReason.SwitchingDisabled, "Sample-rate switching is paused.");
        if (settings.SelectedSourceKey is not { } selected)
            return new(null, RateDecisionReason.NoMasterSelected, "Select an application to control the endpoint sample rate.");

        var depthOverride = settings.FollowSourceBitDepth
            ? settings.ManualBitDepths.FirstOrDefault(item => KeysEqual(item.Key, selected)) : default;
        RateDecision WithDepth(RateDecision decision, int? observed = null) => depthOverride.Key is not null
            ? decision with { DesiredBitsPerSample = depthOverride.Value, ManualBitDepth = true }
            : decision with { DesiredBitsPerSample = settings.FollowSourceBitDepth ? observed : null };

        // Explicit user input applies even if the selected app is paused or has not restarted yet.
        var manual = settings.ManualSampleRatesHz.FirstOrDefault(item => KeysEqual(item.Key, selected));
        if (manual.Key is not null)
            return WithDepth(new(manual.Value, RateDecisionReason.ManualOverride, "Using the selected application's manual sample-rate override."));

        var held = KeysEqual(settings.LastKnownSourceKey, selected) ? settings.LastKnownSampleRateHz : null;
        var current = sources.Where(source => KeysEqual(source.Key, selected)).ToArray();
        if (current.Length == 0)
            return new(held, RateDecisionReason.SourceUnavailable, "Selected application is unavailable; retaining its last confirmed rate.");
        if (current.Length != 1)
            return new(held, RateDecisionReason.NativeRateUnknown, "Ambiguous source sessions; retaining the selected application's last confirmed rate.");

        var source = current[0];
        if (!source.IsActive)
            return new(held, RateDecisionReason.SourcePaused, "Selected application is paused or idle; retaining its last confirmed rate.");

        var eligibleObservations = (observations ?? [])
            .Where(observation => KeysEqual(observation.SourceKey, selected)
                && StringComparer.Ordinal.Equals(observation.SessionToken, source.SessionToken)
                && !string.IsNullOrWhiteSpace(source.SessionToken)
                && observation.SelectionRevision == settings.SelectionRevision
                && observation.SampleRateHz is { } rate && SampleRates.IsValid(rate))
            .ToArray();
        var verifiedRates = eligibleObservations
            .Where(observation => observation.Provenance == RateProvenance.VerifiedNative)
            .Select(observation => observation.SampleRateHz!.Value)
            .Distinct()
            .ToArray();

        if (verifiedRates.Length > 0)
            return verifiedRates.Length == 1
                ? WithDepth(new(verifiedRates[0], RateDecisionReason.VerifiedNative, "Using a verified native rate from the selected application's current sessions."))
                : new(held, RateDecisionReason.ConflictingObservations, "Selected application reports conflicting native rates; use a manual override.");

        var playerFormats = eligibleObservations.Where(observation => observation.Provenance == RateProvenance.PlayerMetadata).ToArray();
        var playerRates = playerFormats.Select(observation => observation.SampleRateHz!.Value).Distinct().ToArray();
        if (playerRates.Length > 1)
            return new(held, RateDecisionReason.ConflictingObservations, "The player reports conflicting track rates; holding output.");
        if (playerRates.Length == 1)
            return WithDepth(new(playerRates[0], RateDecisionReason.PlayerMetadata,
                "Following Fooyin's current track metadata."), settings.FollowSourceBitDepth ? AgreeOnBitDepth(playerFormats) : null);

        var browserFormats = settings.BrowserRateDetectionEnabled
            ? eligibleObservations.Where(observation => observation.Provenance == RateProvenance.BrowserDecoder)
                .ToArray()
            : [];
        var browserRates = browserFormats.Select(observation => observation.SampleRateHz!.Value).Distinct().ToArray();
        if (browserRates.Length > 1)
            return new(held, RateDecisionReason.ConflictingObservations, "Pinned browser tab reports conflicting decoder rates; holding output.");
        if (browserRates.Length == 1)
        {
            var depth = settings.FollowSourceBitDepth ? AgreeOnBitDepth(browserFormats) : null;
            return WithDepth(new(browserRates[0], RateDecisionReason.BrowserDecoder, depth is null
                ? "Following the linked tab's media rate; the browser may resample its output."
                : "Following the linked tab's file rate and bit depth; the browser may convert its output."), depth);
        }

        // Apple Music may keep a fixed Windows PCM rate across hi-res tracks.
        // The explicit catalog option follows track metadata instead, with honest
        // estimate provenance. Missing/stale metadata holds rather than following
        // the endpoint-dependent PCM stream back to the old mixer rate.
        // Catalog precision may justify raising the output depth. The endpoint
        // policy keeps higher output depths to avoid unnecessary Apple restarts.
        var appleMusic = AppleMusicIdentity.IsOfficialExecutable(source.ExecutablePath);
        if (settings.CatalogRateDetectionEnabled && appleMusic)
        {
            var appleFormats = eligibleObservations.Where(observation => observation.Provenance == RateProvenance.CatalogMetadata).ToArray();
            var appleRates = appleFormats.Select(observation => observation.SampleRateHz!.Value).Distinct().ToArray();
            return appleRates.Length switch
            {
                1 => WithDepth(new(appleRates[0], RateDecisionReason.CatalogMetadata,
                    "Following the opted-in Apple Music catalog rate estimate; the delivered asset is unverified."),
                    settings.FollowSourceBitDepth ? AgreeOnBitDepth(appleFormats) : null),
                > 1 => new(held, RateDecisionReason.ConflictingObservations, "Apple Music track metadata is conflicting; retaining the confirmed rate."),
                _ => new(held, RateDecisionReason.NativeRateUnknown, "Apple Music track estimate is pending or unavailable; holding output. Its Windows PCM stream can remain fixed across tracks.")
            };
        }

        var streams = settings.StreamRateDetectionEnabled
            ? eligibleObservations.Where(observation => observation.Provenance == RateProvenance.WasapiStream)
                .ToArray()
            : [];
        var streamRates = streams.Select(observation => observation.SampleRateHz!.Value).Distinct().ToArray();
        if (streamRates.Length > 1)
            return new(held, RateDecisionReason.ConflictingObservations, "Selected app has different simultaneous stream rates; retaining its last confirmed rate.");
        if (streamRates.Length == 1)
        {
            var usesFloat = streams.Any(observation => observation.UsesFloatPcm);
            var executableName = source.ExecutablePath[(Math.Max(source.ExecutablePath.LastIndexOf('\\'),
                source.ExecutablePath.LastIndexOf('/')) + 1)..];
            // These playback engines may convert the decoded track even when they
            // submit integer PCM. Their Windows depth cannot select a track depth.
            var trackDepthUnknown = appleMusic || executableName.ToUpperInvariant() is
                "SPOTIFY.EXE" or "FOOYIN.EXE" or "VLC.EXE" or "CHROME.EXE" or "HELIUM.EXE"
                or "MSEDGE.EXE" or "FIREFOX.EXE" or "ZEN.EXE" or "BRAVE.EXE" or "OPERA.EXE" or "VIVALDI.EXE";
            var bits = settings.FollowSourceBitDepth && !usesFloat && !trackDepthUnknown ? AgreeOnBitDepth(streams) : null;
            var detail = settings.FollowSourceBitDepth && depthOverride.Key is null && (usesFloat || trackDepthUnknown)
                ? "Following the submitted sample rate. Track depth is unconfirmed; keeping the current output depth."
                : "Following the selected app's observed WASAPI PCM stream rate; the app may have resampled its original media.";
            return WithDepth(new(streamRates[0], RateDecisionReason.WasapiStream, detail), bits);
        }

        var catalogRates = settings.CatalogRateDetectionEnabled
            ? eligibleObservations.Where(observation => observation.Provenance == RateProvenance.CatalogMetadata)
                .Select(observation => observation.SampleRateHz!.Value).Distinct().ToArray()
            : [];

        return catalogRates.Length switch
        {
            1 => WithDepth(new(catalogRates[0], RateDecisionReason.CatalogMetadata, "Using an opted-in catalog rate estimate; this does not verify the application's actual stream rate.")),
            > 1 => new(held, RateDecisionReason.ConflictingObservations, "Selected application has conflicting catalog rate estimates; use a manual override."),
            _ => new(held, RateDecisionReason.NativeRateUnknown, "Native rate is not observable; retaining the last confirmed rate. Set a manual override to choose a rate.")
        };
    }

    private static int? AgreeOnBitDepth(IEnumerable<RateObservation> observations)
    {
        var depths = observations.Select(observation => observation.BitsPerSample).Distinct().Take(2).ToArray();
        return depths is [16 or 24 or 32] ? depths[0] : null;
    }

    private static bool KeysEqual(string? left, string? right) => StringComparer.OrdinalIgnoreCase.Equals(left, right);
}

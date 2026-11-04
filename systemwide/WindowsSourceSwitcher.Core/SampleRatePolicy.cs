namespace WindowsSourceSwitcher.Core;

public enum RateProvenance
{
    Unknown,
    WindowsMixFormat,
    ApplicationReported,
    CatalogMetadata,
    VerifiedNative,
    WasapiStream,
    BrowserDecoder
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
    BrowserHold
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

        var browserRates = settings.BrowserRateDetectionEnabled
            ? eligibleObservations.Where(observation => observation.Provenance == RateProvenance.BrowserDecoder)
                .Select(observation => observation.SampleRateHz!.Value).Distinct().ToArray()
            : [];
        if (browserRates.Length > 0)
            return browserRates.Length == 1
                ? WithDepth(new(browserRates[0], RateDecisionReason.BrowserDecoder, "Following the linked tab's media rate; the browser may resample its output."))
                : new(held, RateDecisionReason.ConflictingObservations, "Pinned browser tab reports conflicting decoder rates; holding output.");

        // Apple Music may keep a fixed Windows PCM rate across hi-res tracks.
        // The explicit catalog option follows track metadata instead, with honest
        // estimate provenance. Missing/stale metadata holds rather than following
        // the endpoint-dependent PCM stream back to the old mixer rate.
        if (settings.CatalogRateDetectionEnabled && AppleMusicIdentity.IsOfficialExecutable(source.ExecutablePath))
        {
            var appleFormats = eligibleObservations.Where(observation => observation.Provenance == RateProvenance.CatalogMetadata).ToArray();
            var appleRates = appleFormats.Select(observation => observation.SampleRateHz!.Value).Distinct().ToArray();
            return appleRates.Length switch
            {
                1 => WithDepth(new(appleRates[0], RateDecisionReason.CatalogMetadata, "Following the opted-in Apple Music catalog estimate for this track; the actual delivered asset is unverified."),
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
            var bits = settings.FollowSourceBitDepth && !usesFloat ? AgreeOnBitDepth(streams) : null;
            var spotifyWidth = bits == 32 && source.ExecutablePath.Replace('/', '\\').EndsWith("\\Spotify.exe", StringComparison.OrdinalIgnoreCase);
            var detail = settings.FollowSourceBitDepth && usesFloat && depthOverride.Key is null
                ? "Following the submitted sample rate. The floating-point stream leaves track depth unknown. Choose a master bit depth if known."
                : spotifyWidth && depthOverride.Key is null
                ? "Following Spotify's Windows stream rate. Its 32-bit stream does not establish the track's depth. Choose a master bit-depth override if known."
                : "Following the selected app's observed WASAPI PCM stream rate; the app may have resampled its original media.";
            return WithDepth(new(streamRates[0], RateDecisionReason.WasapiStream, detail), spotifyWidth ? null : bits);
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

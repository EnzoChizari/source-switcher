using System.Globalization;
using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;

namespace WindowsSourceSwitcher.Tray;

internal sealed record SourceRateDisplay(string Text, string Detail, string? Evidence = null);

/// <summary>Labels describe the target and its evidence, never substitute the shared mix rate for a native rate.</summary>
internal static class SourceRatePresentation
{
    public static SourceRateDisplay Describe(PlatformAudioSource source, SourceSettings settings,
        RateObservation? observation = null, RateObservation? streamObservation = null, BrowserDecoderSnapshot? browser = null,
        FavoritePlaybackState playback = FavoritePlaybackState.Unknown)
    {
        var manual = settings.ManualSampleRatesHz.FirstOrDefault(item => KeysEqual(item.Key, source.Identity));
        if (manual.Key is not null && SampleRates.IsValid(manual.Value))
            return new(FormatRate(manual.Value) + " manual",
                "Saved manual target for this app; this is not a detected native/content rate.");

        if (source.SupportsAutomaticRate && settings.CatalogRateDetectionEnabled)
        {
            // Track metadata is meaningful while paused as well. The monitor validates its
            // track/session identity separately; the write policy still requires playback.
            if (IsCurrentCatalogEstimate(source, settings, observation))
                return new(FormatRate(observation!.SampleRateHz!.Value) + " est.",
                    "Apple Music track estimate, independent of the master and mixer; delivered asset remains unverified."
                    + (settings.FollowSourceBitDepth && observation.BitsPerSample is { } depth ? $" Estimated depth is {depth}-bit." : "")
                    + (!source.IsActive || playback == FavoritePlaybackState.NotPlaying ? " Output held during pause." : ""), observation.Detail);
            var heldDetail = KeysEqual(source.Identity, settings.LastKnownSourceKey)
                && settings.LastKnownSampleRateHz is { } appleHeld && SampleRates.IsValid(appleHeld)
                ? " Last confirmed output: " + FormatRate(appleHeld) + "." : "";
            return new("Estimate pending",
                "Track estimate unavailable; holding output. Windows PCM may already be resampled."
                + heldDetail);
        }

        if (!source.IsActive || playback == FavoritePlaybackState.NotPlaying)
        {
            if (!KeysEqual(source.Identity, settings.SelectedSourceKey))
                return new("Idle / paused", "This app is idle or paused; no active stream rate is available.");
            if (KeysEqual(source.Identity, settings.LastKnownSourceKey)
                && settings.LastKnownSampleRateHz is { } held && SampleRates.IsValid(held))
                return new(FormatRate(held) + " held",
                    "Selected app is idle or paused. Holding its last confirmed output rate.");
            return new("Rate held", "Selected app is idle or paused. Current output rate is unchanged.");
        }

        if (settings.FollowSourceBitDepth && observation?.Provenance == RateProvenance.PlayerMetadata
            && IsCurrentObservation(source, settings, observation))
            return new(FormatRate(observation.SampleRateHz!.Value) + " track",
                observation.Detail + (observation.BitsPerSample is { } trackBits ? $" Reported depth: {trackBits}-bit." : " Depth is unknown."),
                observation.Detail);

        if (settings.BrowserRateDetectionEnabled && observation?.Provenance == RateProvenance.BrowserDecoder
            && IsCurrentObservation(source, settings, observation))
            return browser?.EvidenceField == BrowserWireProtocol.ChromiumStreamEvidenceField
                ? new(FormatRate(observation.SampleRateHz!.Value) + " stream header",
                    "Sample rate declared by the linked player's stream initialization data. Browser processing and Windows output are separate.", observation.Detail)
                : browser?.EvidenceField == BrowserWireProtocol.ChromiumHeaderEvidenceField
                ? new(FormatRate(observation.SampleRateHz!.Value) + " file header",
                    (observation.BitsPerSample is { } fileBits ? $"File header reports {fileBits}-bit PCM. " : "")
                    + "Sample rate read from the linked tab's media file. Browser processing and Windows output are separate.", observation.Detail)
                : new(FormatRate(observation.SampleRateHz!.Value) + " tab decoder",
                    "Decoder metadata from the explicitly chosen browser tab. Tab-to-WASAPI routing and later browser processing are unverified.", observation.Detail);

        if (browser is not null && BrowserSourcePolicy.Matches(settings, browser) && browser.DecoderSupported
            && KeysEqual(source.Identity, settings.SelectedSourceKey))
            return new(KeysEqual(settings.LastKnownSourceKey, source.Identity) && settings.LastKnownSampleRateHz is { } browserHeld && SampleRates.IsValid(browserHeld)
                ? FormatRate(browserHeld) + " held" : "Tab rate held", browser.Detail);

        var stream = IsCurrentStream(source, settings, streamObservation) ? streamObservation
            : IsCurrentStream(source, settings, observation) ? observation : null;
        if (stream is not null)
            return new(FormatRate(stream.SampleRateHz!.Value) + " stream",
                settings.FollowSourceBitDepth && string.Equals(Path.GetFileName(source.ExecutablePath), "spotify.exe", StringComparison.OrdinalIgnoreCase)
                    ? "Open Spotify's Lossless badge → File quality to follow its reported depth. The Windows stream only supplies the rate."
                    : "Actual PCM rate submitted to WASAPI; original media rate remains unverified.", stream.Detail);

        if (IsCurrentCatalogEstimate(source, settings, observation))
            return new(FormatRate(observation!.SampleRateHz!.Value) + " est.",
                "Catalog estimate of an available asset; actual stream rate is unverified.", observation.Detail);

        if (settings.StreamRateDetectionEnabled)
            return new("Waiting for stream",
                "No current shared PCM format was confirmed. Refresh retries existing-stream detection. Apps may resample internally.");

        if (!source.SupportsAutomaticRate)
            return new("No detector",
                "Native/content rate is not exposed by Windows audio sessions. Choose a manual rate.");

        if (!settings.CatalogRateDetectionEnabled)
            return new("Estimate off",
                "Apple Music catalog estimates are off. Enable them or choose a manual rate.");

        if (!KeysEqual(source.Identity, settings.SelectedSourceKey))
            return new("Select to estimate", "Select this Apple Music source to request an optional catalog estimate.");

        return new("Estimate pending", "No current Apple Music catalog estimate. Holding output; manual rates remain available.");
    }

    public static string AutoModeText(PlatformAudioSource? source, SourceSettings settings)
    {
        if (source is null || !KeysEqual(source.Identity, settings.SelectedSourceKey))
            return "Hold output / choose a manual rate";
        if (source.SupportsAutomaticRate && settings.CatalogRateDetectionEnabled)
            return "Auto — Apple Music catalog estimate";
        if (settings.StreamRateDetectionEnabled)
            return "Auto — application stream rate (WASAPI)";
        if (!source.SupportsAutomaticRate)
            return "No automatic detector — choose a manual rate";
        return settings.CatalogRateDetectionEnabled
            ? "Auto — Apple Music catalog estimate"
            : "Catalog estimate off — choose a manual rate";
    }

    internal static string FormatRate(int rate) => (rate / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " kHz";

    private static bool IsCurrentStream(PlatformAudioSource source, SourceSettings settings,
        RateObservation? observation) => settings.StreamRateDetectionEnabled
        && IsCurrentObservation(source, settings, observation)
        && observation!.Provenance == RateProvenance.WasapiStream;

    private static bool IsCurrentCatalogEstimate(PlatformAudioSource source, SourceSettings settings,
        RateObservation? observation) => source.SupportsAutomaticRate
        && settings.CatalogRateDetectionEnabled
        && IsCurrentObservation(source, settings, observation, requireActive: false)
        && observation!.Provenance == RateProvenance.CatalogMetadata;

    private static bool IsCurrentObservation(PlatformAudioSource source, SourceSettings settings,
        RateObservation? observation, bool requireActive = true) => (!requireActive || source.IsActive)
        && observation is not null
        && KeysEqual(observation.SourceKey, source.Identity)
        && !string.IsNullOrWhiteSpace(source.SessionToken)
        && StringComparer.Ordinal.Equals(observation.SessionToken, source.SessionToken)
        && observation.SelectionRevision == settings.SelectionRevision
        && observation.SampleRateHz is { } rate && SampleRates.IsValid(rate);

    private static bool KeysEqual(string? left, string? right) => StringComparer.OrdinalIgnoreCase.Equals(left, right);
}

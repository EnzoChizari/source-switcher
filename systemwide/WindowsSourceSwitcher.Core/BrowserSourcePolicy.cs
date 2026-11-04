namespace WindowsSourceSwitcher.Core;

/// <summary>Connects explicit tab evidence to a current application identity, never to an invented tab audio session.</summary>
public static class BrowserSourcePolicy
{
    public static bool Matches(SourceSettings settings, BrowserDecoderSnapshot browser) =>
        settings.BrowserRateDetectionEnabled && browser.IsTabPinned
        && settings.SelectedSourceKey is not null && browser.BrowserExecutablePath is not null
        && StringComparer.OrdinalIgnoreCase.Equals(settings.SelectedSourceKey, browser.BrowserExecutablePath);

    public static RateObservation? Observe(SourceSettings settings, IReadOnlyList<SourceSnapshot> sources,
        BrowserDecoderSnapshot browser, bool fresh)
    {
        if (!Matches(settings, browser) || !fresh || !browser.DecoderSupported
            || browser.State != BrowserDecoderState.Available || browser.DecoderRateHz is not { } rate
            || !SampleRates.IsValid(rate)) return null;
        var current = sources.Where(source => StringComparer.OrdinalIgnoreCase.Equals(source.Key, settings.SelectedSourceKey)).ToArray();
        if (current.Length != 1 || !current[0].IsActive || string.IsNullOrWhiteSpace(current[0].SessionToken)) return null;
        return new(current[0].Key, current[0].SessionToken, settings.SelectionRevision, rate,
            RateProvenance.BrowserDecoder, browser.Detail);
    }

    public static RateDecision HoldIfRequired(SourceSettings settings, BrowserDecoderSnapshot browser,
        RateObservation? observation, RateDecision decision)
    {
        if (!settings.SwitchingEnabled || !Matches(settings, browser) || !browser.DecoderSupported
            || observation is not null || decision.Reason is RateDecisionReason.ManualOverride or RateDecisionReason.VerifiedNative) return decision;
        var held = StringComparer.OrdinalIgnoreCase.Equals(settings.LastKnownSourceKey, settings.SelectedSourceKey)
            ? settings.LastKnownSampleRateHz : null;
        return new(held, RateDecisionReason.BrowserHold, browser.Detail);
    }
}

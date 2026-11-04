using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Core.Tests;

public sealed class BrowserRatePolicyTests
{
    private const string Browser = @"C:\Program Files\imput\Helium\Application\chrome.exe";
    private const string Token = "browser-current-session";

    [Theory]
    [InlineData(true, 96000, RateDecisionReason.BrowserDecoder)]
    [InlineData(false, 48000, RateDecisionReason.WasapiStream)]
    public void DecoderOptInControlsWhetherTabEvidenceCanReplaceSubmittedPcm(bool enabled, int expected,
        RateDecisionReason reason)
    {
        var settings = Settings();
        settings.BrowserRateDetectionEnabled = enabled;
        var decision = SampleRatePolicy.Evaluate(settings, [Source()],
            [Evidence(settings, 96000), Evidence(settings, 48000, RateProvenance.WasapiStream)]);
        Assert.Equal(expected, decision.DesiredSampleRateHz);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(44100, settings.LastKnownSampleRateHz);
    }

    [Fact]
    public void BrowserDecoderCanBeEnabledIndependentlyOfWindowsStreamDetection()
    {
        var settings = Settings();
        settings.StreamRateDetectionEnabled = false;
        var decision = SampleRatePolicy.Evaluate(settings, [Source()], [Evidence(settings, 192000)]);
        Assert.Equal(192000, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.BrowserDecoder, decision.Reason);
        Assert.Contains("media rate", decision.Detail);
        Assert.Contains("resample", decision.Detail);
    }

    [Fact]
    public void ManualOverrideTakesPriorityOverBrowserAndConflictingAutomaticEvidence()
    {
        var settings = Settings();
        settings.ManualSampleRatesHz[Browser] = 88200;
        settings.CatalogRateDetectionEnabled = true;
        var decision = SampleRatePolicy.Evaluate(settings, [Source()],
            [Evidence(settings, 96000), Evidence(settings, 192000),
                Evidence(settings, 48000, RateProvenance.VerifiedNative),
                Evidence(settings, 176400, RateProvenance.VerifiedNative),
                Evidence(settings, 48000, RateProvenance.WasapiStream),
                Evidence(settings, 352800, RateProvenance.CatalogMetadata)]);
        Assert.Equal(88200, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.ManualOverride, decision.Reason);
    }

    [Fact]
    public void IndependentlyVerifiedContentEvidenceTakesPriorityOverBrowserMetadata()
    {
        var settings = Settings();
        var decision = SampleRatePolicy.Evaluate(settings, [Source()],
            [Evidence(settings, 192000), Evidence(settings, 96000, RateProvenance.VerifiedNative),
                Evidence(settings, 48000, RateProvenance.WasapiStream)]);
        Assert.Equal(96000, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.VerifiedNative, decision.Reason);
    }

    [Fact]
    public void ConflictingVerifiedContentDoesNotFallThroughToOneBrowserRate()
    {
        var settings = Settings();
        var decision = SampleRatePolicy.Evaluate(settings, [Source()],
            [Evidence(settings, 192000), Evidence(settings, 96000, RateProvenance.VerifiedNative),
                Evidence(settings, 48000, RateProvenance.VerifiedNative)]);
        Assert.Equal(44100, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.ConflictingObservations, decision.Reason);
    }

    [Fact]
    public void ConflictingBrowserRatesHoldInsteadOfChoosingWindowsPcmOrCatalog()
    {
        var settings = Settings();
        settings.CatalogRateDetectionEnabled = true;
        var decision = SampleRatePolicy.Evaluate(settings, [Source()],
            [Evidence(settings, 96000), Evidence(settings, 192000),
                Evidence(settings, 48000, RateProvenance.WasapiStream),
                Evidence(settings, 352800, RateProvenance.CatalogMetadata)]);
        Assert.Equal(44100, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.ConflictingObservations, decision.Reason);
    }

    [Fact]
    public void DuplicateDecoderEvidenceDoesNotCreateAFalseConflict()
    {
        var settings = Settings();
        var observation = Evidence(settings, 96000);
        var decision = SampleRatePolicy.Evaluate(settings, [Source()], [observation, observation]);
        Assert.Equal(96000, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.BrowserDecoder, decision.Reason);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("session")]
    [InlineData("revision")]
    [InlineData("future-revision")]
    public void BrowserEvidenceForAnotherSelectionOrSessionCannotOverrideCurrentPcm(string mismatch)
    {
        var settings = Settings();
        var stale = Evidence(settings, 192000);
        stale = mismatch switch
        {
            "source" => stale with { SourceKey = @"C:\Apps\Other\browser.exe" },
            "session" => stale with { SessionToken = "previous-session" },
            "revision" => stale with { SelectionRevision = settings.SelectionRevision - 1 },
            _ => stale with { SelectionRevision = settings.SelectionRevision + 1 }
        };
        var decision = SampleRatePolicy.Evaluate(settings, [Source()],
            [stale, Evidence(settings, 48000, RateProvenance.WasapiStream)]);
        Assert.Equal(48000, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.WasapiStream, decision.Reason);
    }

    [Fact]
    public void EmptySessionIdentityCannotAuthorizeBrowserEvidence()
    {
        var settings = Settings();
        var decision = SampleRatePolicy.Evaluate(settings, [Source() with { SessionToken = "" }],
            [Evidence(settings, 192000) with { SessionToken = "" }]);
        Assert.Equal(44100, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.NativeRateUnknown, decision.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-96000)]
    [InlineData(7999)]
    [InlineData(768001)]
    public void UnknownOrImplausibleBrowserRatesCannotBecomeAnEndpointTarget(int? rate)
    {
        var settings = Settings();
        settings.LastKnownSourceKey = null;
        settings.LastKnownSampleRateHz = null;
        var decision = SampleRatePolicy.Evaluate(settings, [Source()], [Evidence(settings, rate)]);
        Assert.Null(decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.NativeRateUnknown, decision.Reason);
    }

    [Theory]
    [InlineData(true, RateDecisionReason.SourcePaused)]
    [InlineData(false, RateDecisionReason.SourceUnavailable)]
    public void PausedOrMissingSelectedBrowserRetainsItsConfirmedRate(bool present, RateDecisionReason reason)
    {
        var settings = Settings();
        var decision = SampleRatePolicy.Evaluate(settings,
            present ? [Source() with { IsActive = false }] : [], [Evidence(settings, 192000)]);
        Assert.Equal(44100, decision.DesiredSampleRateHz);
        Assert.Equal(reason, decision.Reason);
    }

    [Fact]
    public void DisabledSwitchingIgnoresAnOtherwiseFreshBrowserObservation()
    {
        var settings = Settings();
        settings.SwitchingEnabled = false;
        var decision = SampleRatePolicy.Evaluate(settings, [Source()], [Evidence(settings, 96000)]);
        Assert.Null(decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.SwitchingDisabled, decision.Reason);
    }

    private static SourceSettings Settings()
    {
        var settings = new SourceSettings { BrowserRateDetectionEnabled = true };
        settings.SelectMaster(Browser, "Helium");
        settings.RememberAppliedRate(Browser, 44100);
        return settings;
    }

    private static SourceSnapshot Source() => new(Browser, "Helium", Browser, [100, 200], true, Token);
    private static RateObservation Evidence(SourceSettings settings, int? rate,
        RateProvenance provenance = RateProvenance.BrowserDecoder) =>
        new(Browser, Token, settings.SelectionRevision, rate, provenance, "Receiver-validated selected-tab evidence");
}

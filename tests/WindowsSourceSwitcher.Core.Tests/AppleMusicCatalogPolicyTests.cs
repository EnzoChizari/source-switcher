using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Core.Tests;

public sealed class AppleMusicCatalogPolicyTests
{
    private const string Main = @"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.1540.23042.0_x64__nzyj5cx40ttqa\AppleMusic.exe";

    [Theory]
    [InlineData(true, 192000, RateDecisionReason.CatalogMetadata)]
    [InlineData(false, 44100, RateDecisionReason.WasapiStream)]
    public void ExplicitAppleCatalogOptInCanTargetContentEstimateInsteadOfTheAppsFixedSubmittedPcm(bool enabled,
        int expectedRate, RateDecisionReason reason)
    {
        var settings = Settings();
        settings.CatalogRateDetectionEnabled = enabled;
        var result = SampleRatePolicy.Evaluate(settings, [Source()],
            [Evidence(settings, 192000, RateProvenance.CatalogMetadata), Evidence(settings, 44100, RateProvenance.WasapiStream)]);
        Assert.Equal(expectedRate, result.DesiredSampleRateHz);
        Assert.Equal(reason, result.Reason);
        if (enabled) Assert.Contains("estimate", result.Detail);
        Assert.Equal(96000, settings.LastKnownSampleRateHz);
    }

    [Fact]
    public void MissingOptedInAppleEstimateHoldsInsteadOfCreatingASubmittedPcmFeedbackLoop()
    {
        var settings = Settings();
        var result = SampleRatePolicy.Evaluate(settings, [Source()], [Evidence(settings, 44100, RateProvenance.WasapiStream)]);
        Assert.Equal(96000, result.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.NativeRateUnknown, result.Reason);
    }

    [Fact]
    public void UnknownEstimateWithoutHistoryDoesNotInventAnOutputRate()
    {
        var settings = Settings();
        settings.LastKnownSourceKey = null;
        settings.LastKnownSampleRateHz = null;
        var result = SampleRatePolicy.Evaluate(settings, [Source()],
            [Evidence(settings, null, RateProvenance.CatalogMetadata), Evidence(settings, 44100, RateProvenance.WasapiStream)]);
        Assert.Null(result.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.NativeRateUnknown, result.Reason);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("revision")]
    [InlineData("source")]
    public void StaleOrUnrelatedCatalogMetadataCannotOverrideTheSelectedAppleTrack(string mismatch)
    {
        var settings = Settings();
        var stale = Evidence(settings, 192000, RateProvenance.CatalogMetadata);
        stale = mismatch switch
        {
            "session" => stale with { SessionToken = "old-session" },
            "revision" => stale with { SelectionRevision = settings.SelectionRevision - 1 },
            _ => stale with { SourceKey = @"C:\Other\AppleMusic.exe" }
        };
        var result = SampleRatePolicy.Evaluate(settings, [Source()], [stale, Evidence(settings, 44100, RateProvenance.WasapiStream)]);
        Assert.Equal(96000, result.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.NativeRateUnknown, result.Reason);
    }

    [Fact]
    public void ConflictingAppleCatalogValuesHoldRatherThanPickingFixedPcm()
    {
        var settings = Settings();
        var result = SampleRatePolicy.Evaluate(settings, [Source()],
            [Evidence(settings, 192000, RateProvenance.CatalogMetadata), Evidence(settings, 96000, RateProvenance.CatalogMetadata),
                Evidence(settings, 44100, RateProvenance.WasapiStream)]);
        Assert.Equal(96000, result.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.ConflictingObservations, result.Reason);
    }

    [Fact]
    public void ManualAndIndependentlyVerifiedRatesStillTakePriority()
    {
        var settings = Settings();
        var observations = new[] { Evidence(settings, 192000, RateProvenance.CatalogMetadata),
            Evidence(settings, 44100, RateProvenance.WasapiStream), Evidence(settings, 88200, RateProvenance.VerifiedNative) };
        var verified = SampleRatePolicy.Evaluate(settings, [Source()], observations);
        Assert.Equal(88200, verified.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.VerifiedNative, verified.Reason);
        settings.ManualSampleRatesHz[Main] = 48000;
        var manual = SampleRatePolicy.Evaluate(settings, [Source()], observations);
        Assert.Equal(48000, manual.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.ManualOverride, manual.Reason);
    }

    [Fact]
    public void OtherApplicationsDoNotAcquireAppleCatalogPriorityFromAGlobalPreference()
    {
        const string spotify = @"C:\Apps\Spotify.exe";
        var settings = Settings();
        settings.SelectMaster(spotify, "Spotify");
        var source = new SourceSnapshot(spotify, "Spotify", spotify, [100], true, "current-session");
        var result = SampleRatePolicy.Evaluate(settings, [source],
            [new(spotify, source.SessionToken, settings.SelectionRevision, 192000, RateProvenance.CatalogMetadata, "Unrelated estimate"),
                new(spotify, source.SessionToken, settings.SelectionRevision, 48000, RateProvenance.WasapiStream, "Actual PCM")]);
        Assert.Equal(48000, result.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.WasapiStream, result.Reason);
    }

    private static SourceSettings Settings()
    {
        var settings = new SourceSettings { CatalogRateDetectionEnabled = true };
        settings.SelectMaster(Main, "AppleMusic");
        settings.RememberAppliedRate(Main, 96000);
        return settings;
    }
    private static SourceSnapshot Source() => new(Main, "AppleMusic", Main, [100, 200], true, "current-session");
    private static RateObservation Evidence(SourceSettings settings, int? rate, RateProvenance provenance) =>
        new(Main, "current-session", settings.SelectionRevision, rate, provenance, "Current selected track metadata");
}

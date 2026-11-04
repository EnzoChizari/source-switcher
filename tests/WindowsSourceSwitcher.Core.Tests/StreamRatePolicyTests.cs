using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Core.Tests;

public sealed class StreamRatePolicyTests
{
    private const string Key = @"C:\Apps\Spotify\Spotify.exe";
    private static SourceSnapshot Source(bool active = true) => new(Key, "Spotify", Key, [100, 200], active, "current-session");

    [Fact]
    public void ObservedStreamRateRequiresDetectionEnabled()
    {
        var settings = Settings();
        settings.StreamRateDetectionEnabled = false;
        var decision = SampleRatePolicy.Evaluate(settings, [Source()], [Observation(settings, 48_000)]);
        Assert.Equal(44_100, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.NativeRateUnknown, decision.Reason);
    }

    [Fact]
    public void EnabledObservedStreamRateIsLabeledAsStreamEvidence()
    {
        var settings = Settings();
        var decision = SampleRatePolicy.Evaluate(settings, [Source()], [Observation(settings, 48_000)]);
        Assert.Equal(48_000, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.WasapiStream, decision.Reason);
        Assert.Contains("resampled", decision.Detail);
        Assert.Equal(44_100, settings.LastKnownSampleRateHz);
    }

    [Fact]
    public void ManualOverrideWinsOverAllAutomaticEvidence()
    {
        var settings = Settings();
        settings.ManualSampleRatesHz[Key] = 88_200;
        settings.CatalogRateDetectionEnabled = true;
        var decision = SampleRatePolicy.Evaluate(settings, [Source()],
            [Observation(settings, 48_000), Observation(settings, 96_000),
             Observation(settings, 192_000, RateProvenance.VerifiedNative),
             Observation(settings, 352_800, RateProvenance.CatalogMetadata)]);
        Assert.Equal(88_200, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.ManualOverride, decision.Reason);
    }

    [Fact]
    public void VerifiedNativeRateWinsOverObservedStreamAndCatalog()
    {
        var settings = Settings();
        settings.CatalogRateDetectionEnabled = true;
        var decision = SampleRatePolicy.Evaluate(settings, [Source()],
            [Observation(settings, 48_000), Observation(settings, 96_000, RateProvenance.VerifiedNative),
             Observation(settings, 192_000, RateProvenance.CatalogMetadata)]);
        Assert.Equal(96_000, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.VerifiedNative, decision.Reason);
    }

    [Fact]
    public void ObservedStreamWinsOverCatalogEstimate()
    {
        var settings = Settings();
        settings.CatalogRateDetectionEnabled = true;
        var decision = SampleRatePolicy.Evaluate(settings, [Source()],
            [Observation(settings, 48_000), Observation(settings, 192_000, RateProvenance.CatalogMetadata)]);
        Assert.Equal(48_000, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.WasapiStream, decision.Reason);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("session")]
    [InlineData("revision")]
    public void StaleOrUnrelatedStreamEvidenceCannotChangeRate(string mismatch)
    {
        var settings = Settings();
        var observation = Observation(settings, 96_000);
        observation = mismatch switch
        {
            "source" => observation with { SourceKey = @"C:\Apps\Other.exe" },
            "session" => observation with { SessionToken = "previous-session" },
            _ => observation with { SelectionRevision = settings.SelectionRevision - 1 }
        };
        var decision = SampleRatePolicy.Evaluate(settings, [Source()], [observation]);
        Assert.Equal(44_100, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.NativeRateUnknown, decision.Reason);
    }

    [Fact]
    public void DifferentSimultaneousStreamRatesHoldLastConfirmedRate()
    {
        var settings = Settings();
        settings.CatalogRateDetectionEnabled = true;
        var decision = SampleRatePolicy.Evaluate(settings, [Source()],
            [Observation(settings, 48_000), Observation(settings, 96_000),
             Observation(settings, 192_000, RateProvenance.CatalogMetadata)]);
        Assert.Equal(44_100, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.ConflictingObservations, decision.Reason);
    }

    [Fact]
    public void MatchingSimultaneousStreamRatesProduceOneDecision()
    {
        var settings = Settings();
        var decision = SampleRatePolicy.Evaluate(settings, [Source()],
            [Observation(settings, 48_000), Observation(settings, 48_000)]);
        Assert.Equal(48_000, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.WasapiStream, decision.Reason);
    }

    [Fact]
    public void PauseHoldsConfirmedRateDespitePreviouslyObservedStream()
    {
        var settings = Settings();
        var decision = SampleRatePolicy.Evaluate(settings, [Source(active: false)], [Observation(settings, 96_000)]);
        Assert.Equal(44_100, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.SourcePaused, decision.Reason);
    }

    [Fact]
    public void UnobservedStreamWithoutHistoryRequestsNoChange()
    {
        var settings = Settings();
        settings.LastKnownSourceKey = null;
        settings.LastKnownSampleRateHz = null;
        Assert.Null(SampleRatePolicy.Evaluate(settings, [Source()]).DesiredSampleRateHz);
    }

    private static SourceSettings Settings()
    {
        var settings = new SourceSettings { StreamRateDetectionEnabled = true };
        settings.SelectMaster(Key, "Spotify");
        settings.RememberAppliedRate(Key, 44_100);
        return settings;
    }

    private static RateObservation Observation(SourceSettings settings, int rate,
        RateProvenance provenance = RateProvenance.WasapiStream) =>
        new(Key, "current-session", settings.SelectionRevision, rate, provenance, "Test stream evidence");
}

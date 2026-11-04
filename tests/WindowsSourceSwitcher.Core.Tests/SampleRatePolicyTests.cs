using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Core.Tests;

public sealed class SampleRatePolicyTests
{
    private const string PlayerKey = @"C:\Apps\Music\player.exe";
    private const string OtherKey = @"C:\Apps\Browser\browser.exe";

    [Fact]
    public void DepthIsOptInAndRequiresUnambiguousCurrentIntegerPcm()
    {
        var settings = SelectedWithHeldRate();
        var stream = Observation(settings, 48000) with { Provenance = RateProvenance.WasapiStream, PcmBitsPerSample = 24 };
        Assert.Null(SampleRatePolicy.Evaluate(settings, [Source()], [stream]).DesiredBitsPerSample);
        settings.FollowSourceBitDepth = true;
        Assert.Equal(24, SampleRatePolicy.Evaluate(settings, [Source()], [stream]).DesiredBitsPerSample);
        Assert.Null(SampleRatePolicy.Evaluate(settings, [Source()], [stream, stream with { PcmBitsPerSample = null }]).DesiredBitsPerSample);
        Assert.Null(SampleRatePolicy.Evaluate(settings, [Source(active: false)], [stream]).DesiredBitsPerSample);
        Assert.Null(SampleRatePolicy.Evaluate(settings, [Source(token: "new-session")], [stream]).DesiredBitsPerSample);
        settings.ManualSampleRatesHz[PlayerKey] = 48000;
        Assert.Null(SampleRatePolicy.Evaluate(settings, [Source()], [stream]).DesiredBitsPerSample);
    }

    [Fact]
    public void NoSelectionDoesNotUseAnActiveApplication()
    {
        var decision = SampleRatePolicy.Evaluate(new(), [Source(OtherKey)]);
        Assert.Null(decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.NoMasterSelected, decision.Reason);
    }

    [Fact]
    public void SelectingADifferentMasterNeverInheritsPreviousRate()
    {
        var settings = SelectedWithHeldRate();
        settings.SelectMaster(OtherKey, "Browser");
        Assert.Null(settings.LastKnownSampleRateHz);
        Assert.Null(SampleRatePolicy.Evaluate(settings, [Source(OtherKey)]).DesiredSampleRateHz);
    }

    [Fact]
    public void PausePreservesRateAndIgnoresOtherActiveApps()
    {
        var settings = SelectedWithHeldRate();
        var decision = SampleRatePolicy.Evaluate(settings, [Source(active: false), Source(OtherKey)],
            [Observation(settings, 192000), Observation(settings, 48000) with { SourceKey = OtherKey }]);
        Assert.Equal(44100, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.SourcePaused, decision.Reason);
    }

    [Fact]
    public void MissingMasterKeepsHeldRateWithoutFallingBack()
    {
        var settings = SelectedWithHeldRate();
        var decision = SampleRatePolicy.Evaluate(settings, [Source(OtherKey)],
            [Observation(settings, 48000) with { SourceKey = OtherKey }]);
        Assert.Equal(44100, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.SourceUnavailable, decision.Reason);
    }

    [Fact]
    public void RestartWithNewPidRemainsSelectedAndAcceptsNewSessionEvidence()
    {
        var settings = SelectedWithHeldRate();
        var restarted = Source(token: "new-session") with { ProcessIds = [9001] };
        var decision = SampleRatePolicy.Evaluate(settings, [restarted],
            [Observation(settings, 96000) with { SessionToken = "new-session" }]);
        Assert.Equal(96000, decision.DesiredSampleRateHz);
        Assert.Equal(PlayerKey, settings.SelectedSourceKey);
        // Evaluation is pure; a failed endpoint operation must not persist an unapplied rate.
        Assert.Equal(44100, settings.LastKnownSampleRateHz);
    }

    [Fact]
    public void RestartRejectsEvidenceFromAnOldSession()
    {
        var settings = SelectedWithHeldRate();
        var decision = SampleRatePolicy.Evaluate(settings, [Source(token: "new-session")], [Observation(settings, 192000)]);
        Assert.Equal(44100, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.NativeRateUnknown, decision.Reason);
    }

    [Fact]
    public void ReselectionRejectsEvidenceFromEarlierSelectionEvenForSameSource()
    {
        var settings = SelectedWithHeldRate();
        var oldObservation = Observation(settings, 192000);
        settings.SelectMaster(PlayerKey, "Player");
        var decision = SampleRatePolicy.Evaluate(settings, [Source()], [oldObservation]);
        Assert.Equal(44100, decision.DesiredSampleRateHz);
    }

    [Theory]
    [InlineData(RateProvenance.Unknown)]
    [InlineData(RateProvenance.WindowsMixFormat)]
    [InlineData(RateProvenance.ApplicationReported)]
    [InlineData(RateProvenance.CatalogMetadata)]
    public void UnverifiedRatesDoNotPretendToBeNative(RateProvenance provenance)
    {
        var settings = SelectedWithHeldRate();
        var decision = SampleRatePolicy.Evaluate(settings, [Source()], [Observation(settings, 192000) with { Provenance = provenance }]);
        Assert.Equal(44100, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.NativeRateUnknown, decision.Reason);
    }

    [Fact]
    public void OptedInCatalogEstimateIsExplicitlyLabeled()
    {
        var settings = SelectedWithHeldRate();
        settings.CatalogRateDetectionEnabled = true;
        var decision = SampleRatePolicy.Evaluate(settings, [Source()],
            [Observation(settings, 96000) with { Provenance = RateProvenance.CatalogMetadata }]);
        Assert.Equal(96000, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.CatalogMetadata, decision.Reason);
        Assert.Contains("estimate", decision.Detail);
    }

    [Fact]
    public void VerifiedNativeEvidenceTakesPriorityOverCatalogEstimate()
    {
        var settings = SelectedWithHeldRate();
        settings.CatalogRateDetectionEnabled = true;
        var decision = SampleRatePolicy.Evaluate(settings, [Source()],
            [Observation(settings, 48000), Observation(settings, 192000) with { Provenance = RateProvenance.CatalogMetadata }]);
        Assert.Equal(48000, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.VerifiedNative, decision.Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ManualOverrideAppliesWhilePausedOrMissing(bool present)
    {
        var settings = SelectedWithHeldRate();
        settings.ManualSampleRatesHz[PlayerKey] = 88200;
        var decision = SampleRatePolicy.Evaluate(settings, present ? [Source(active: false)] : [], [Observation(settings, 192000)]);
        Assert.Equal(88200, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.ManualOverride, decision.Reason);
    }

    [Fact]
    public void ManualOverrideTakesPriorityOverConflictingEvidence()
    {
        var settings = SelectedWithHeldRate();
        settings.ManualSampleRatesHz[PlayerKey.ToUpperInvariant()] = 48000;
        var decision = SampleRatePolicy.Evaluate(settings, [Source()], [Observation(settings, 96000), Observation(settings, 192000)]);
        Assert.Equal(48000, decision.DesiredSampleRateHz);
    }

    [Fact]
    public void ConflictingVerifiedRatesHoldInsteadOfPickingTheFirst()
    {
        var settings = SelectedWithHeldRate();
        var decision = SampleRatePolicy.Evaluate(settings, [Source()], [Observation(settings, 96000), Observation(settings, 192000)]);
        Assert.Equal(44100, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.ConflictingObservations, decision.Reason);
    }

    [Fact]
    public void IdenticalVerifiedRatesFromMultipleSessionsAreNotAConflict()
    {
        var settings = SelectedWithHeldRate();
        var decision = SampleRatePolicy.Evaluate(settings, [Source()], [Observation(settings, 96000), Observation(settings, 96000)]);
        Assert.Equal(96000, decision.DesiredSampleRateHz);
    }

    [Fact]
    public void ConflictingCatalogEstimatesHoldEvenWhenOptedIn()
    {
        var settings = SelectedWithHeldRate();
        settings.CatalogRateDetectionEnabled = true;
        var decision = SampleRatePolicy.Evaluate(settings, [Source()],
            [Observation(settings, 96000) with { Provenance = RateProvenance.CatalogMetadata },
             Observation(settings, 192000) with { Provenance = RateProvenance.CatalogMetadata }]);
        Assert.Equal(44100, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.ConflictingObservations, decision.Reason);
    }

    [Fact]
    public void MissingSessionIdentityCannotBeTreatedAsFreshVerifiedEvidence()
    {
        var settings = SelectedWithHeldRate();
        var decision = SampleRatePolicy.Evaluate(settings, [Source(token: "")],
            [Observation(settings, 192000) with { SessionToken = "" }]);
        Assert.Equal(44100, decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.NativeRateUnknown, decision.Reason);
    }

    [Fact]
    public void UnknownRateWithoutHistoryRequestsNoChange()
    {
        var settings = new SourceSettings();
        settings.SelectMaster(PlayerKey, "Player");
        var decision = SampleRatePolicy.Evaluate(settings, [Source()]);
        Assert.Null(decision.DesiredSampleRateHz);
    }

    [Fact]
    public void DisabledSwitchingDoesNotRequestEvenAnExplicitOverride()
    {
        var settings = SelectedWithHeldRate();
        settings.SwitchingEnabled = false;
        settings.ManualSampleRatesHz[PlayerKey] = 96000;
        Assert.Null(SampleRatePolicy.Evaluate(settings, [Source()]).DesiredSampleRateHz);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-44100)]
    [InlineData(7999)]
    [InlineData(768001)]
    public void ImplausibleVerifiedValuesAreIgnored(int hz)
    {
        var settings = SelectedWithHeldRate();
        Assert.Equal(44100, SampleRatePolicy.Evaluate(settings, [Source()], [Observation(settings, hz)]).DesiredSampleRateHz);
    }

    [Fact]
    public void RememberingAppliedRateRequiresTheCurrentlySelectedMaster()
    {
        var settings = SelectedWithHeldRate();
        Assert.Throws<InvalidOperationException>(() => settings.RememberAppliedRate(OtherKey, 96000));
        settings.RememberAppliedRate(PlayerKey, 96000);
        Assert.Equal(96000, settings.LastKnownSampleRateHz);
    }

    [Fact]
    public void SourceIdentityIgnoresCaseAndSlashStyle()
    {
        Assert.Equal(SourceIdentity.FromExecutablePath(@"c:\Apps\player.exe"),
            SourceIdentity.FromExecutablePath("C:/apps/PLAYER.EXE"));
        Assert.Throws<ArgumentException>(() => SourceIdentity.FromExecutablePath("player.exe"));
    }

    private static SourceSettings SelectedWithHeldRate()
    {
        var settings = new SourceSettings();
        settings.SelectMaster(PlayerKey, "Player");
        settings.RememberAppliedRate(PlayerKey, 44100);
        return settings;
    }

    private static SourceSnapshot Source(string key = PlayerKey, bool active = true, string token = "session-1") =>
        new(key, "Player", key, [100], active, token);

    private static RateObservation Observation(SourceSettings settings, int rate) =>
        new(PlayerKey, "session-1", settings.SelectionRevision, rate, RateProvenance.VerifiedNative, "Test-provided source evidence");
}

using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Core.Tests;

public sealed class BrowserSourcePolicyTests
{
    private const string Browser = @"C:\Program Files\Zen Browser\zen.exe";

    [Fact]
    public void CurrentDecoderEvidenceBindsToCurrentSessionAndSelectionRatherThanInventingATabSession()
    {
        var settings = Settings();
        var source = Source();
        var observation = BrowserSourcePolicy.Observe(settings, [source], Snapshot(), fresh: true);
        Assert.NotNull(observation);
        Assert.Equal(source.Key, observation.SourceKey);
        Assert.Equal(source.SessionToken, observation.SessionToken);
        Assert.Equal(settings.SelectionRevision, observation.SelectionRevision);
        Assert.Equal(96000, observation.SampleRateHz);
        Assert.Equal(RateProvenance.BrowserDecoder, observation.Provenance);
        Assert.Equal("Chosen decoder evidence", observation.Detail);
        Assert.Equal(44100, settings.LastKnownSampleRateHz);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("unfollowed")]
    [InlineData("other-browser")]
    [InlineData("missing-browser-path")]
    [InlineData("no-master")]
    public void ObservationAndHoldRequireEnabledExplicitPinForTheSelectedExecutable(string mismatch)
    {
        var settings = Settings();
        var snapshot = Snapshot();
        switch (mismatch)
        {
            case "disabled": settings.BrowserRateDetectionEnabled = false; break;
            case "unfollowed": snapshot = snapshot with { IsTabPinned = false }; break;
            case "other-browser": snapshot = snapshot with { BrowserExecutablePath = @"C:\Other\zen.exe" }; break;
            case "missing-browser-path": snapshot = snapshot with { BrowserExecutablePath = null }; break;
            default:
                settings.SelectedSourceKey = null;
                settings.SelectedSourceDisplayName = null;
                settings.LastKnownSourceKey = null;
                settings.LastKnownSampleRateHz = null;
                break;
        }
        Assert.False(BrowserSourcePolicy.Matches(settings, snapshot));
        Assert.Null(BrowserSourcePolicy.Observe(settings, [Source()], snapshot, fresh: true));
        var streamDecision = new RateDecision(48000, RateDecisionReason.WasapiStream, "Current Windows PCM");
        Assert.Same(streamDecision, BrowserSourcePolicy.HoldIfRequired(settings, snapshot, null, streamDecision));
    }

    [Fact]
    public void ExecutableIdentityMatchingUsesWindowsCaseSemantics()
    {
        var settings = Settings();
        var snapshot = Snapshot() with { BrowserExecutablePath = Browser.ToUpperInvariant() };
        Assert.True(BrowserSourcePolicy.Matches(settings, snapshot));
        Assert.NotNull(BrowserSourcePolicy.Observe(settings, [Source()], snapshot, fresh: true));
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    [InlineData("blank-session")]
    public void MissingOrAmbiguousCurrentAudioSessionCannotAuthorizeBrowserRate(string mismatch)
    {
        var settings = Settings();
        SourceSnapshot[] sources = mismatch switch
        {
            "inactive" => [Source() with { IsActive = false }],
            "missing" => [],
            "ambiguous" => [Source(), Source() with { SessionToken = "different-session" }],
            _ => [Source() with { SessionToken = " " }]
        };
        Assert.Null(BrowserSourcePolicy.Observe(settings, sources, Snapshot(), fresh: true));
    }

    [Theory]
    [InlineData(BrowserDecoderState.Paused)]
    [InlineData(BrowserDecoderState.Conflict)]
    [InlineData(BrowserDecoderState.Unavailable)]
    [InlineData(BrowserDecoderState.Disconnected)]
    [InlineData(BrowserDecoderState.TimedOut)]
    [InlineData(BrowserDecoderState.WaitingForProbe)]
    public void SupportedPinnedAdapterWithoutUsableDecoderEvidenceHoldsConfirmedTarget(BrowserDecoderState state)
    {
        var settings = Settings();
        var snapshot = Snapshot() with { State = state };
        var observation = BrowserSourcePolicy.Observe(settings, [Source()], snapshot, fresh: true);
        Assert.Null(observation);
        var result = BrowserSourcePolicy.HoldIfRequired(settings, snapshot, observation,
            new(48000, RateDecisionReason.WasapiStream, "Current Windows PCM"));
        Assert.Equal(44100, result.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.BrowserHold, result.Reason);
        Assert.Equal(snapshot.Detail, result.Detail);
    }

    [Fact]
    public void StaleAvailableEvidenceCannotAuthorizeNewRateAndHoldsInsteadOfFollowingBrowserOutput()
    {
        var settings = Settings();
        var snapshot = Snapshot() with { OutputRateHz = 192000 };
        var observation = BrowserSourcePolicy.Observe(settings, [Source()], snapshot, fresh: false);
        Assert.Null(observation);
        var result = BrowserSourcePolicy.HoldIfRequired(settings, snapshot, observation,
            new(192000, RateDecisionReason.WasapiStream, "Browser output PCM"));
        Assert.Equal(44100, result.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.BrowserHold, result.Reason);
    }

    [Fact]
    public void UnsupportedDecoderCapabilityAllowsActualWindowsPcmFallback()
    {
        var settings = Settings();
        var snapshot = Snapshot() with { DecoderSupported = false, DecoderRateHz = null,
            State = BrowserDecoderState.Unavailable };
        Assert.True(BrowserSourcePolicy.Matches(settings, snapshot));
        Assert.Null(BrowserSourcePolicy.Observe(settings, [Source()], snapshot, fresh: true));
        var streamDecision = new RateDecision(48000, RateDecisionReason.WasapiStream, "Actual submitted PCM");
        Assert.Same(streamDecision, BrowserSourcePolicy.HoldIfRequired(settings, snapshot, null, streamDecision));
    }

    [Theory]
    [InlineData(RateDecisionReason.ManualOverride)]
    [InlineData(RateDecisionReason.VerifiedNative)]
    public void HigherPriorityUserOrVerifiedContentDecisionsSurviveUnavailableBrowserEvidence(RateDecisionReason reason)
    {
        var settings = Settings();
        var snapshot = Snapshot() with { State = BrowserDecoderState.Unavailable, DecoderRateHz = null };
        var decision = new RateDecision(88200, reason, "Higher priority decision");
        Assert.Same(decision, BrowserSourcePolicy.HoldIfRequired(settings, snapshot, null, decision));
    }

    [Fact]
    public void FreshDecoderObservationDoesNotGetReplacedByAHold()
    {
        var settings = Settings();
        var snapshot = Snapshot();
        var observation = BrowserSourcePolicy.Observe(settings, [Source()], snapshot, fresh: true);
        Assert.NotNull(observation);
        var decision = SampleRatePolicy.Evaluate(settings, [Source()], [observation]);
        Assert.Same(decision, BrowserSourcePolicy.HoldIfRequired(settings, snapshot, observation, decision));
        Assert.Equal(RateDecisionReason.BrowserDecoder, decision.Reason);
    }

    [Fact]
    public void BrowserHoldWithoutSuccessfulHistoryDoesNotInventARate()
    {
        var settings = Settings();
        settings.LastKnownSourceKey = null;
        settings.LastKnownSampleRateHz = null;
        var decision = BrowserSourcePolicy.HoldIfRequired(settings, Snapshot(), null,
            new(48000, RateDecisionReason.WasapiStream, "Current PCM"));
        Assert.Null(decision.DesiredSampleRateHz);
        Assert.Equal(RateDecisionReason.BrowserHold, decision.Reason);
    }

    [Fact]
    public void DisabledSwitchingDoesNotGetChangedIntoABrowserHold()
    {
        var settings = Settings();
        settings.SwitchingEnabled = false;
        var disabled = new RateDecision(null, RateDecisionReason.SwitchingDisabled, "Paused by user");
        Assert.Same(disabled, BrowserSourcePolicy.HoldIfRequired(settings, Snapshot(), null, disabled));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(7999)]
    [InlineData(768001)]
    public void UnsupportedDecoderRateValuesNeverBecomeObservations(int? rate)
    {
        Assert.Null(BrowserSourcePolicy.Observe(Settings(), [Source()], Snapshot() with { DecoderRateHz = rate }, fresh: true));
    }

    private static SourceSettings Settings()
    {
        var settings = new SourceSettings { BrowserRateDetectionEnabled = true };
        settings.SelectMaster(Browser, "Zen");
        settings.RememberAppliedRate(Browser, 44100);
        return settings;
    }
    private static SourceSnapshot Source() => new(Browser, "Zen", Browser, [100, 200], true, "current-session");
    private static BrowserDecoderSnapshot Snapshot() => new(1, Browser.ToUpperInvariant(), 100,
        DateTimeOffset.Parse("2026-10-20T00:00:00Z"), true, 7, "document", 1, false, 96000, 48000,
        BrowserDecoderState.Available, "Chosen decoder evidence", 1234, "browser-lifetime", "Tab")
        { EvidenceField = BrowserWireProtocol.EvidenceField };
}

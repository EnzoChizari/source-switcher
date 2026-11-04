using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Core.Tests;

public sealed class FavoriteAutoSwitchPolicyTests
{
    private const string A = @"C:\Apps\A.exe";
    private const string B = @"C:\Apps\B.exe";
    private const string C = @"C:\Apps\C.exe";
    private const string D = @"C:\Apps\D.exe";

    [Fact]
    public void CurrentlyPlayingMasterIsNeverPreemptedByAnotherPlayingFavorite()
    {
        var settings = Settings(A, A, B);
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A, FavoritePlaybackState.Playing), (B, FavoritePlaybackState.Playing));
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 0).TargetSourceKey);
        var decision = Poll(policy, settings, [Live(B), Live(A)], playback, 30000);
        Assert.Null(decision.TargetSourceKey);
        Assert.Null(decision.RetryAfter);
        Assert.Equal(A, settings.SelectedSourceKey);
    }

    [Fact]
    public void StoppedAndPlayingConfirmationsOverlapForAHalfSecondHandoff()
    {
        var settings = Settings(A, A, B);
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A, FavoritePlaybackState.NotPlaying), (B, FavoritePlaybackState.Playing));
        Assert.Equal(Ms(500), FavoriteAutoSwitchPolicy.StopDebounce);
        Assert.Equal(Ms(500), FavoriteAutoSwitchPolicy.PlayingDebounce);
        Assert.Equal(Ms(500), Poll(policy, settings, [Live(A), Live(B)], playback, 0).RetryAfter);
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 499).TargetSourceKey);
        Assert.Equal(Identity(B), Poll(policy, settings, [Live(A), Live(B)], playback, 500).TargetSourceKey);
    }

    [Theory]
    [InlineData(FavoritePlaybackState.Unknown)]
    [InlineData((FavoritePlaybackState)99)]
    public void UnknownPlaybackDoesNotTreatSilenceAsAPause(FavoritePlaybackState state)
    {
        var settings = Settings(A, A, B);
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A, state), (B, FavoritePlaybackState.Playing));
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 0).TargetSourceKey);
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 30000).TargetSourceKey);
    }

    [Fact]
    public void MissingPlaybackEvidenceForAnActiveMasterKeepsItSelected()
    {
        var settings = Settings(A, A, B);
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((B, FavoritePlaybackState.Playing));
        Poll(policy, settings, [Live(A), Live(B)], playback, 0);
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 10000).TargetSourceKey);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingOrInactiveMasterCannotRemainPlayingThroughStaleEvidence(bool missing)
    {
        var settings = Settings(A, A, B);
        var policy = new FavoriteAutoSwitchPolicy();
        SourceSnapshot[] live = missing ? [Live(B)] : [Live(A) with { IsActive = false }, Live(B)];
        var playback = Playback((A, FavoritePlaybackState.Playing), (B, FavoritePlaybackState.Playing));
        Poll(policy, settings, live, playback, 0);
        Assert.Equal(Identity(B), Poll(policy, settings, live, playback, 500).TargetSourceKey);
    }

    [Theory]
    [InlineData(A, B)]
    [InlineData(B, C)]
    [InlineData(C, A)]
    public void NextPlayingFavoriteUsesStableExecutableOrderAndWraps(string selected, string expected)
    {
        var settings = Settings(selected, C, B, A);
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A, FavoritePlaybackState.Playing), (B, FavoritePlaybackState.Playing),
            (C, FavoritePlaybackState.Playing));
        playback[selected] = FavoritePlaybackState.NotPlaying;
        Poll(policy, settings, [Live(C), Live(B), Live(A)], playback, 0);
        Assert.Equal(Identity(expected), Poll(policy, settings, [Live(B), Live(A), Live(C)], playback, 500).TargetSourceKey);
    }

    [Fact]
    public void ClosedCurrentFavoriteStillAnchorsTheNextPosition()
    {
        var settings = Settings(C, A, B, C, D);
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A, FavoritePlaybackState.Playing), (B, FavoritePlaybackState.Playing),
            (D, FavoritePlaybackState.Playing));
        Poll(policy, settings, [Live(A), Live(B), Live(D)], playback, 0);
        Assert.Equal(Identity(D), Poll(policy, settings, [Live(D), Live(A), Live(B)], playback, 500).TargetSourceKey);
    }

    [Theory]
    [InlineData(FavoritePlaybackState.Unknown)]
    [InlineData(FavoritePlaybackState.NotPlaying)]
    public void CandidateRequiresExplicitPlayingEvidence(FavoritePlaybackState candidateState)
    {
        var settings = Settings(A, A, B);
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A, FavoritePlaybackState.NotPlaying), (B, candidateState));
        Poll(policy, settings, [Live(A), Live(B)], playback, 0);
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 10000).TargetSourceKey);
        playback.Remove(B);
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 20000).TargetSourceKey);
    }

    [Fact]
    public void UnpinnedPlayingSourceNeverBecomesTheFallback()
    {
        var settings = Settings(A, A);
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A, FavoritePlaybackState.NotPlaying), (B, FavoritePlaybackState.Playing));
        Poll(policy, settings, [Live(A), Live(B)], playback, 0);
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 10000).TargetSourceKey);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void BothGeneralSwitchingAndTheOptInAreRequired(bool switching, bool automatic)
    {
        var settings = Settings(A, A, B);
        settings.SwitchingEnabled = switching;
        settings.FavoriteAutoSwitchEnabled = automatic;
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A, FavoritePlaybackState.NotPlaying), (B, FavoritePlaybackState.Playing));
        Poll(policy, settings, [Live(A), Live(B)], playback, 0);
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 10000).TargetSourceKey);
    }

    [Fact]
    public void ReleaseOrManuallySelectedNonfavoriteDoesNotCreateAFavoriteMaster()
    {
        var settings = Settings(null, A, B);
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A, FavoritePlaybackState.Playing), (B, FavoritePlaybackState.Playing));
        Poll(policy, settings, [Live(A), Live(B)], playback, 0);
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 10000).TargetSourceKey);
        settings.SelectMaster(C, "Nonfavorite");
        Poll(policy, settings, [Live(A), Live(B)], playback, 11000);
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 30000).TargetSourceKey);
        Assert.Equal(C, settings.SelectedSourceKey);
    }

    [Fact]
    public void TransientPauseOrBufferingDoesNotHandOffTheMaster()
    {
        var settings = Settings(A, A, B);
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A, FavoritePlaybackState.NotPlaying), (B, FavoritePlaybackState.Playing));
        Poll(policy, settings, [Live(A), Live(B)], playback, 0);
        playback[A] = FavoritePlaybackState.Playing;
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 400).TargetSourceKey);
        playback[A] = FavoritePlaybackState.NotPlaying;
        Assert.Equal(Ms(500), Poll(policy, settings, [Live(A), Live(B)], playback, 2000).RetryAfter);
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 2499).TargetSourceKey);
        Assert.Equal(Identity(B), Poll(policy, settings, [Live(A), Live(B)], playback, 2500).TargetSourceKey);
    }

    [Fact]
    public void UnknownCurrentEvidenceRestartsTheStopDebounce()
    {
        var settings = Settings(A, A, B);
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A, FavoritePlaybackState.NotPlaying), (B, FavoritePlaybackState.Playing));
        Poll(policy, settings, [Live(A), Live(B)], playback, 0);
        playback[A] = FavoritePlaybackState.Unknown;
        Poll(policy, settings, [Live(A), Live(B)], playback, 400);
        playback[A] = FavoritePlaybackState.NotPlaying;
        Poll(policy, settings, [Live(A), Live(B)], playback, 2000);
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 2499).TargetSourceKey);
        Assert.Equal(Identity(B), Poll(policy, settings, [Live(A), Live(B)], playback, 2500).TargetSourceKey);
    }

    [Fact]
    public void LatePlayingCandidateMustRemainPlayingForHalfASecond()
    {
        var settings = Settings(A, A, B);
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A, FavoritePlaybackState.NotPlaying), (B, FavoritePlaybackState.Unknown));
        Poll(policy, settings, [Live(A), Live(B)], playback, 0);
        playback[B] = FavoritePlaybackState.Playing;
        Assert.Equal(Ms(500), Poll(policy, settings, [Live(A), Live(B)], playback, 5000).RetryAfter);
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 5499).TargetSourceKey);
        Assert.Equal(Identity(B), Poll(policy, settings, [Live(A), Live(B)], playback, 5500).TargetSourceKey);
    }

    [Fact]
    public void CandidateChangesRestartItsPlaybackConfirmation()
    {
        var settings = Settings(A, A, B, C);
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A, FavoritePlaybackState.NotPlaying), (B, FavoritePlaybackState.Playing),
            (C, FavoritePlaybackState.Playing));
        Poll(policy, settings, [Live(A), Live(B), Live(C)], playback, 0);
        playback[B] = FavoritePlaybackState.Unknown;
        Assert.Equal(Ms(500), Poll(policy, settings, [Live(A), Live(B), Live(C)], playback, 499).RetryAfter);
        Assert.Null(Poll(policy, settings, [Live(A), Live(B), Live(C)], playback, 998).TargetSourceKey);
        Assert.Equal(Identity(C), Poll(policy, settings, [Live(A), Live(B), Live(C)], playback, 999).TargetSourceKey);
    }

    [Fact]
    public void CandidateBecomingUnknownCannotReuseItsEarlierPlayingTime()
    {
        var settings = Settings(A, A, B);
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A, FavoritePlaybackState.NotPlaying), (B, FavoritePlaybackState.Playing));
        Poll(policy, settings, [Live(A), Live(B)], playback, 0);
        playback[B] = FavoritePlaybackState.Unknown;
        Poll(policy, settings, [Live(A), Live(B)], playback, 499);
        playback[B] = FavoritePlaybackState.Playing;
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 500).TargetSourceKey);
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 999).TargetSourceKey);
        Assert.Equal(Identity(B), Poll(policy, settings, [Live(A), Live(B)], playback, 1000).TargetSourceKey);
    }

    [Fact]
    public void TheNextMasterMustStopForANewHalfSecondBeforeAnotherHandoff()
    {
        var settings = Settings(A, A, B);
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A, FavoritePlaybackState.NotPlaying), (B, FavoritePlaybackState.Playing));
        Poll(policy, settings, [Live(A), Live(B)], playback, 0);
        Assert.Equal(Identity(B), Poll(policy, settings, [Live(A), Live(B)], playback, 500).TargetSourceKey);

        settings.SelectMaster(B, "B");
        playback[A] = FavoritePlaybackState.Playing;
        playback[B] = FavoritePlaybackState.NotPlaying;
        Assert.Equal(Ms(500), Poll(policy, settings, [Live(A), Live(B)], playback, 501).RetryAfter);
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 1000).TargetSourceKey);
        Assert.Equal(Identity(A), Poll(policy, settings, [Live(A), Live(B)], playback, 1001).TargetSourceKey);
    }

    [Fact]
    public void SettingsOrSelectionChangesRestartAllPendingTimers()
    {
        var settings = Settings(A, A, B);
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A, FavoritePlaybackState.NotPlaying), (B, FavoritePlaybackState.Playing));
        Poll(policy, settings, [Live(A), Live(B)], playback, 0);
        settings.SelectMaster(A, "A again");
        Assert.Equal(Ms(500), Poll(policy, settings, [Live(A), Live(B)], playback, 500).RetryAfter);
        settings.FavoriteAutoSwitchEnabled = false;
        Poll(policy, settings, [Live(A), Live(B)], playback, 1300);
        settings.FavoriteAutoSwitchEnabled = true;
        Assert.Equal(Ms(500), Poll(policy, settings, [Live(A), Live(B)], playback, 5000).RetryAfter);
        settings.SwitchingEnabled = false;
        Poll(policy, settings, [Live(A), Live(B)], playback, 5100);
        settings.SwitchingEnabled = true;
        Assert.Equal(Ms(500), Poll(policy, settings, [Live(A), Live(B)], playback, 10000).RetryAfter);
        Assert.Equal(Identity(B), Poll(policy, settings, [Live(A), Live(B)], playback, 10500).TargetSourceKey);
    }

    [Fact]
    public void ChangingTheSelectedKeyDirectlyStillRestartsTheDebounce()
    {
        var settings = Settings(A, A, B, C);
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A, FavoritePlaybackState.NotPlaying), (B, FavoritePlaybackState.Playing),
            (C, FavoritePlaybackState.NotPlaying));
        Poll(policy, settings, [Live(A), Live(B), Live(C)], playback, 0);
        settings.SelectedSourceKey = C;
        Assert.Equal(Ms(500), Poll(policy, settings, [Live(A), Live(B), Live(C)], playback, 5000).RetryAfter);
        Assert.Equal(Identity(B), Poll(policy, settings, [Live(A), Live(B), Live(C)], playback, 5500).TargetSourceKey);
    }

    [Fact]
    public void RemovingTheSelectedFavoriteCancelsAPendingHandoff()
    {
        var settings = Settings(A, A, B);
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A, FavoritePlaybackState.NotPlaying), (B, FavoritePlaybackState.Playing));
        Poll(policy, settings, [Live(A), Live(B)], playback, 0);
        settings.SetFavorite(A, "A", false);
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 500).TargetSourceKey);
    }

    [Fact]
    public void OwnFormatChangeHasABoundedRecoveryThenAFullNewStopDebounce()
    {
        var settings = Settings(A, A, B);
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A, FavoritePlaybackState.NotPlaying), (B, FavoritePlaybackState.Playing));
        Poll(policy, settings, [Live(A), Live(B)], playback, 0);
        policy.SuspendForRecovery(Ms(1000), Ms(3000));
        Assert.Equal(Ms(1), Poll(policy, settings, [Live(B)], playback, 3999).RetryAfter);
        Assert.Equal(Ms(500), Poll(policy, settings, [Live(B)], playback, 4000).RetryAfter);
        Assert.Null(Poll(policy, settings, [Live(B)], playback, 4499).TargetSourceKey);
        Assert.Equal(Identity(B), Poll(policy, settings, [Live(B)], playback, 4500).TargetSourceKey);
    }

    [Fact]
    public void RestoredPlayingMasterSurvivesTheEndOfRecovery()
    {
        var settings = Settings(A, A, B);
        var policy = new FavoriteAutoSwitchPolicy();
        policy.SuspendForRecovery(Ms(0), Ms(3000));
        var playback = Playback((A, FavoritePlaybackState.Playing), (B, FavoritePlaybackState.Playing));
        Assert.Null(Poll(policy, settings, [Live(B)], playback, 1000).TargetSourceKey);
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 3000).TargetSourceKey);
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 30000).TargetSourceKey);
    }

    [Fact]
    public void ManualSelectionEndsAnEarlierMastersRecovery()
    {
        var settings = Settings(A, A, B, C);
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A, FavoritePlaybackState.NotPlaying), (B, FavoritePlaybackState.Playing),
            (C, FavoritePlaybackState.NotPlaying));
        Poll(policy, settings, [Live(A), Live(B), Live(C)], playback, 0);
        policy.SuspendForRecovery(Ms(1000), Ms(3000));
        settings.SelectMaster(C, "C");
        Assert.Equal(Ms(500), Poll(policy, settings, [Live(A), Live(B), Live(C)], playback, 1001).RetryAfter);
        Assert.Equal(Identity(B), Poll(policy, settings, [Live(A), Live(B), Live(C)], playback, 1501).TargetSourceKey);
    }

    [Fact]
    public void ClockGoingBackwardFailsClosedAndRestartsConfirmation()
    {
        var settings = Settings(A, A, B);
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A, FavoritePlaybackState.NotPlaying), (B, FavoritePlaybackState.Playing));
        Poll(policy, settings, [Live(A), Live(B)], playback, 100000);
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 100).TargetSourceKey);
        Assert.Equal(Ms(500), Poll(policy, settings, [Live(A), Live(B)], playback, 101).RetryAfter);
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 600).TargetSourceKey);
        Assert.Equal(Identity(B), Poll(policy, settings, [Live(A), Live(B)], playback, 601).TargetSourceKey);
    }

    [Fact]
    public void OnlyActivePersistableMatchingFavoriteSnapshotsCanBeTargets()
    {
        var settings = Settings(A, A, B, C, D);
        settings.FavoriteSources[@"C:\Apps\ShareX.exe"] = "ShareX";
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((B, FavoritePlaybackState.Playing), (C, FavoritePlaybackState.Playing),
            (D, FavoritePlaybackState.Playing), (@"C:\Apps\ShareX.exe", FavoritePlaybackState.Playing));
        SourceSnapshot[] live = [Live(B) with { IsActive = false }, Live(C) with { ExecutablePath = D },
            Live(D) with { Key = "unavailable:123" }, Live(@"C:\Apps\ShareX.exe"), Live("system-sounds")];
        Poll(policy, settings, live, playback, 0);
        Assert.Null(Poll(policy, settings, live, playback, 10000).TargetSourceKey);
    }

    [Fact]
    public void IdentityCaseSlashesPidsAndSessionTokensDoNotChangeTheDecision()
    {
        var settings = Settings(A.ToLowerInvariant(), A, B);
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A.ToLowerInvariant().Replace('\\', '/'), FavoritePlaybackState.NotPlaying),
            (B.ToLowerInvariant().Replace('\\', '/'), FavoritePlaybackState.Playing));
        SourceSnapshot[] live = [Live(A), Live(B) with { Key = B.ToLowerInvariant(), ExecutablePath = B.Replace('\\', '/') }];
        Poll(policy, settings, live, playback, 0);
        live[1] = live[1] with { ProcessIds = [500], SessionToken = "restarted" };
        Assert.Equal(Identity(B), Poll(policy, settings, live, playback, 500).TargetSourceKey);
    }

    [Fact]
    public void DuplicateConflictingIdentityEvidenceFailsClosed()
    {
        var settings = Settings(A, A, B);
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A, FavoritePlaybackState.NotPlaying), (B, FavoritePlaybackState.Playing),
            (B.ToLowerInvariant(), FavoritePlaybackState.NotPlaying));
        Poll(policy, settings, [Live(A), Live(B)], playback, 0);
        Assert.Null(Poll(policy, settings, [Live(A), Live(B)], playback, 10000).TargetSourceKey);
    }

    [Fact]
    public void ADecisionDoesNotMutateSettingsOrTheHeldRate()
    {
        var settings = Settings(A, A, B);
        settings.RememberAppliedRate(A, 96000);
        var revision = settings.SelectionRevision;
        var policy = new FavoriteAutoSwitchPolicy();
        var playback = Playback((A, FavoritePlaybackState.NotPlaying), (B, FavoritePlaybackState.Playing));
        Poll(policy, settings, [Live(A), Live(B)], playback, 0);
        Assert.Equal(Identity(B), Poll(policy, settings, [Live(A), Live(B)], playback, 500).TargetSourceKey);
        Assert.Equal(A, settings.SelectedSourceKey);
        Assert.Equal(revision, settings.SelectionRevision);
        Assert.Equal(96000, settings.LastKnownSampleRateHz);
    }

    private static SourceSettings Settings(string? selected, params string[] favorites)
    {
        var settings = new SourceSettings { FavoriteAutoSwitchEnabled = true };
        foreach (var path in favorites) settings.SetFavorite(path, path, true);
        if (selected is not null) settings.SelectMaster(selected, selected);
        return settings;
    }

    private static Dictionary<string, FavoritePlaybackState> Playback(params (string Key, FavoritePlaybackState State)[] states) =>
        states.ToDictionary(entry => entry.Key, entry => entry.State, StringComparer.Ordinal);

    private static FavoriteAutoSwitchDecision Poll(FavoriteAutoSwitchPolicy policy, SourceSettings settings,
        IReadOnlyList<SourceSnapshot> live, IReadOnlyDictionary<string, FavoritePlaybackState> playback, int milliseconds) =>
        policy.Update(settings, live, playback, Ms(milliseconds));

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);
    private static string Identity(string path) => SourceIdentity.FromExecutablePath(path);
    private static SourceSnapshot Live(string path) => new(path, "Player", path, [123], true, "session");
}

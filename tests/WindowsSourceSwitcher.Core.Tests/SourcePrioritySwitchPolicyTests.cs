using Xunit;

namespace WindowsSourceSwitcher.Core.Tests;

public sealed class SourcePrioritySwitchPolicyTests
{
    private const string Favorite = @"C:\APPS\FAVORITE.EXE", Pin = @"C:\APPS\PIN.EXE", Regular = @"C:\APPS\REGULAR.EXE", Other = @"C:\APPS\OTHER.EXE";
    private const string Apple = @"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.5.0.0_x64__nzyj5cx40ttqa\AppleMusic.exe";
    private const string AppleHelper = @"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.5.0.0_x64__nzyj5cx40ttqa\AMPLibraryAgent.exe";
    private static SourceSnapshot Live(string key) => new(key, Path.GetFileName(key), key, [1], true, key);
    private static SourceSettings Settings(string? current = Regular)
    {
        var settings = new SourceSettings { FavoriteAutoSwitchEnabled = true };
        if (current is not null) settings.SelectMaster(current, "Current");
        settings.SetFavorite(Favorite, "Favorite", true);
        settings.SetPinned(Pin, "Pinned", true);
        return settings;
    }
    private static FavoriteAutoSwitchDecision Poll(SourcePrioritySwitchPolicy policy, SourceSettings settings, int milliseconds, params string[] playing)
        => policy.Update(settings, playing.Select(Live).ToArray(),
            playing.ToDictionary(key => key, _ => FavoritePlaybackState.Playing), TimeSpan.FromMilliseconds(milliseconds));

    [Theory]
    [InlineData(Apple, 0)]
    [InlineData(Apple, 1)]
    [InlineData(Apple, 2)]
    [InlineData(AppleHelper, 0)]
    [InlineData(AppleHelper, 1)]
    [InlineData(AppleHelper, 2)]
    public void IncomingAppleMusicUsesHalfASecondInEveryTier(string target, int tier)
    {
        var settings = Settings();
        if (tier == 0) settings.SetFavorite(target, "Apple Music", true);
        if (tier == 1) settings.SetPinned(target, "Apple Music", true);
        var policy = new SourcePrioritySwitchPolicy();
        var decision = Poll(policy, settings, 0, target);
        Assert.Equal(TimeSpan.FromMilliseconds(500), decision.RetryAfter);
        Assert.Null(Poll(policy, settings, 499, target).TargetSourceKey);
        Assert.Equal(target.ToUpperInvariant(), Poll(policy, settings, 500, target).TargetSourceKey);
    }

    [Theory]
    [InlineData(Favorite)]
    [InlineData(Pin)]
    [InlineData(Regular)]
    [InlineData(@"C:\Apps\AppleMusic.exe")]
    public void LeavingAppleMusicUsesHalfASecondForEveryOtherTarget(string target)
    {
        var settings = Settings(Apple);
        var policy = new SourcePrioritySwitchPolicy();
        Assert.Equal(TimeSpan.FromMilliseconds(500), Poll(policy, settings, 0, target).RetryAfter);
        Assert.Null(Poll(policy, settings, 499, target).TargetSourceKey);
        Assert.Equal(target.ToUpperInvariant(), Poll(policy, settings, 500, target).TargetSourceKey);
    }

    [Fact]
    public void ChangingBetweenAppleAndOtherCandidatesStartsTheNewTargetsOwnDelay()
    {
        var settings = Settings(Other);
        var policy = new SourcePrioritySwitchPolicy();
        Poll(policy, settings, 0, Apple);
        Assert.Null(Poll(policy, settings, 3900, Pin).TargetSourceKey);
        Assert.Null(Poll(policy, settings, 4399, Pin).TargetSourceKey);
        Assert.Equal(Pin, Poll(policy, settings, 4400, Pin).TargetSourceKey);
        Assert.Null(Poll(policy, settings, 4500, Apple).TargetSourceKey);
        Assert.Null(Poll(policy, settings, 4999, Apple).TargetSourceKey);
        Assert.Equal(Apple.ToUpperInvariant(), Poll(policy, settings, 5000, Apple).TargetSourceKey);
    }

    [Fact]
    public void AppleCandidateMustRemainConfirmedPlayingForTheWholeHalfSecond()
    {
        var settings = Settings(Other);
        var policy = new SourcePrioritySwitchPolicy();
        Poll(policy, settings, 0, Apple);
        Assert.Null(policy.Update(settings, [Live(Apple)],
            new Dictionary<string, FavoritePlaybackState> { [Apple] = FavoritePlaybackState.Unknown },
            TimeSpan.FromMilliseconds(400)).TargetSourceKey);
        Assert.False(policy.HasPendingHandoff);
        Assert.Null(Poll(policy, settings, 500, Apple).TargetSourceKey);
        Assert.Null(Poll(policy, settings, 999, Apple).TargetSourceKey);
        Assert.Equal(Apple.ToUpperInvariant(), Poll(policy, settings, 1000, Apple).TargetSourceKey);
    }

    [Theory]
    [InlineData(true, true, Favorite)]
    [InlineData(false, true, Pin)]
    [InlineData(false, false, Regular)]
    public void ChoosesHighestPlayingTierAfterHalfASecond(bool favorite, bool pin, string expected)
    {
        var settings = Settings(Other);
        var policy = new SourcePrioritySwitchPolicy();
        var playing = new List<string> { Regular };
        if (favorite) playing.Add(Favorite);
        if (pin) playing.Add(Pin);
        Assert.Null(Poll(policy, settings, 0, playing.ToArray()).TargetSourceKey);
        Assert.Null(Poll(policy, settings, 499, playing.ToArray()).TargetSourceKey);
        Assert.Equal(expected, Poll(policy, settings, 500, playing.ToArray()).TargetSourceKey);
    }

    [Fact]
    public void HigherTierPreemptsPlayingLowerTierButPlayingPeersStaySelected()
    {
        var settings = Settings();var policy = new SourcePrioritySwitchPolicy();
        Poll(policy, settings, 0, Regular, Pin);
        Assert.Equal(Pin, Poll(policy, settings, 500, Regular, Pin).TargetSourceKey);
        settings.SelectMaster(Pin, "Pinned");
        Poll(policy, settings, 600, Regular, Pin, Favorite);
        Assert.Equal(Favorite, Poll(policy, settings, 1100, Regular, Pin, Favorite).TargetSourceKey);
        settings.SetFavorite(Pin, "Pinned", true);
        Assert.Null(Poll(policy, settings, 1200, Pin, Favorite).TargetSourceKey);
    }

    [Fact]
    public void CandidateChangesOrCurrentResumeRestartTheConfirmation()
    {
        var settings = Settings(Favorite);var policy = new SourcePrioritySwitchPolicy();
        Poll(policy, settings, 0, Pin, Regular);
        Assert.Null(Poll(policy, settings, 400, Regular).TargetSourceKey);
        Assert.Null(Poll(policy, settings, 600, Favorite, Regular).TargetSourceKey);
        Assert.Null(Poll(policy, settings, 900, Pin).TargetSourceKey);
        Assert.Equal(Pin, Poll(policy, settings, 1400, Pin).TargetSourceKey);
    }

    [Fact]
    public void ReleaseIsRespectedAndFullscreenExitCanExplicitlyEnrollWithoutAnOldMaster()
    {
        var settings = Settings(null);var policy = new SourcePrioritySwitchPolicy();
        Assert.Null(Poll(policy, settings, 0, Favorite).TargetSourceKey);
        var sources = new[] { Live(Favorite) };
        var states = new Dictionary<string, FavoritePlaybackState> { [Favorite] = FavoritePlaybackState.Playing };
        Assert.Null(policy.Update(settings, sources, states, TimeSpan.FromSeconds(1), true).TargetSourceKey);
        Assert.Equal(Favorite, policy.Update(settings, sources, states, TimeSpan.FromMilliseconds(1500), true).TargetSourceKey);
        Assert.Null(policy.Update(settings, sources, states, TimeSpan.FromSeconds(2), false).TargetSourceKey);
    }

    [Fact]
    public void UnknownPlaybackAndInvalidOrExcludedIdentitiesCannotBecomeMasters()
    {
        var settings = Settings();var policy = new SourcePrioritySwitchPolicy();
        var sources = new[] { Live(Favorite), Live(Regular), Live(@"C:\Apps\ShareX.exe"), Live("system-sounds") };
        var states = sources.ToDictionary(s => s.Key, _ => FavoritePlaybackState.Playing);
        states[Regular] = FavoritePlaybackState.Unknown;
        Assert.Null(policy.Update(settings, sources, states, TimeSpan.Zero).TargetSourceKey);
        settings.SelectMaster(Other, "Closed");states[Favorite] = FavoritePlaybackState.Unknown;states[Regular] = FavoritePlaybackState.NotPlaying;
        Assert.Null(policy.Update(settings, sources, states, TimeSpan.FromSeconds(1)).TargetSourceKey);
        Assert.Null(policy.Update(settings, sources, states, TimeSpan.FromSeconds(2)).TargetSourceKey);
    }

    [Fact]
    public void RecoveryGraceDisableAndReversedClockAreBounded()
    {
        var settings = Settings();var policy = new SourcePrioritySwitchPolicy();
        policy.SuspendForRecovery(TimeSpan.Zero, TimeSpan.FromSeconds(3));
        Assert.Null(Poll(policy, settings, 1000, Favorite).TargetSourceKey);
        Assert.Null(Poll(policy, settings, 3000, Favorite).TargetSourceKey);
        Assert.Equal(Favorite, Poll(policy, settings, 3500, Favorite).TargetSourceKey);
        Assert.Null(Poll(policy, settings, 100, Favorite).TargetSourceKey);
        settings.FavoriteAutoSwitchEnabled = false;
        Assert.Null(Poll(policy, settings, 4000, Favorite).TargetSourceKey);
        Assert.False(policy.HasPendingHandoff);
    }

    [Fact]
    public async Task PinsPersistIndependentlyOfFavoritesAndRejectDuplicatesAndShareX()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dx5-pins-" + Guid.NewGuid());
        try
        {
            var settings = Settings();var revision = settings.SelectionRevision;
            Assert.True(settings.IsPinned(Pin.ToLowerInvariant()));
            Assert.False(settings.IsFavorite(Pin));
            Assert.Throws<ArgumentException>(() => settings.SetPinned(@"C:\Apps\ShareX.exe", "ShareX", true));
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            await store.SaveAsync(settings);var loaded = await store.LoadAsync();
            Assert.Null(loaded.Warning);Assert.True(loaded.Settings.IsPinned(Pin));
            Assert.Equal(revision, loaded.Settings.SelectionRevision);
            Assert.True(loaded.Settings.SetPinned(Pin.ToLowerInvariant(), "Pinned", false));
            Assert.Empty(loaded.Settings.PinnedSources);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}


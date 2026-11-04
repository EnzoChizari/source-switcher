using Windows.Media.Control;
using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class FavoritePlaybackMonitorTests
{
    private const string ApplePath = @"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.2.3.4_x64__nzyj5cx40ttqa\AppleMusic.exe";
    private const string SpotifyPath = @"C:\Users\Listener\AppData\Roaming\Spotify\Spotify.exe";

    [Theory]
    [InlineData(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, FavoritePlaybackState.Playing)]
    [InlineData(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, FavoritePlaybackState.NotPlaying)]
    [InlineData(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped, FavoritePlaybackState.NotPlaying)]
    [InlineData(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed, FavoritePlaybackState.NotPlaying)]
    [InlineData(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing, FavoritePlaybackState.Unknown)]
    [InlineData(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened, FavoritePlaybackState.Unknown)]
    public void MapsTransportStatusWithoutUsingAudioPeaks(
        GlobalSystemMediaTransportControlsSessionPlaybackStatus status, FavoritePlaybackState expected)
        => Assert.Equal(expected, FavoritePlaybackMonitor.MapPlaybackStatus(status));

    [Fact]
    public void UnknownWindowsStatusFailsClosed()
        => Assert.Equal(FavoritePlaybackState.Unknown,
            FavoritePlaybackMonitor.MapPlaybackStatus((GlobalSystemMediaTransportControlsSessionPlaybackStatus)999));

    [Theory]
    [InlineData(FavoritePlaybackSnapshot.AppleMusicAppId)]
    [InlineData("AppleMusic.exe")]
    [InlineData("APPLEINC.APPLEMUSICWIN_NZYJ5CX40TTQA!APP")]
    public void OfficialAppleMusicAssociatesItsPackageOrLegacyExecutableId(string appId)
    {
        var source = Source(ApplePath);
        Assert.Equal(FavoritePlaybackState.NotPlaying,
            Snapshot(appId, FavoritePlaybackState.NotPlaying).Resolve([source])[source.Identity]);
        Assert.True(source.IsActive); // A lagging Windows session does not override a confirmed media pause.
    }

    [Theory]
    [InlineData(@"C:\Apps\AppleMusic.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.2.3.4_x64__other\AppleMusic.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.2.3.4_x64__nzyj5cx40ttqa\Other.exe")]
    public void AppleSessionDoesNotAssociateAnArbitraryExecutable(string path)
    {
        var source = Source(path);
        Assert.Equal(FavoritePlaybackState.Unknown,
            Snapshot(FavoritePlaybackSnapshot.AppleMusicAppId, FavoritePlaybackState.Playing).Resolve([source])[source.Identity]);
        Assert.Equal(FavoritePlaybackState.Unknown,
            Snapshot("AppleMusic.exe", FavoritePlaybackState.Playing).Resolve([source])[source.Identity]);
    }

    [Theory]
    [InlineData("AppleInc.AppleMusicWin!App")]
    [InlineData("AppleInc.AppleMusicWin_nzyj5cx40ttqa!DifferentApp")]
    [InlineData("prefix-AppleInc.AppleMusicWin_nzyj5cx40ttqa!App")]
    [InlineData("AppleInc.AppleMusicWin_other!App")]
    public void ApplePackageIdMustMatchTheKnownApplicationExactly(string appId)
    {
        var source = Source(ApplePath);
        Assert.Equal(FavoritePlaybackState.Unknown,
            Snapshot(appId, FavoritePlaybackState.Playing).Resolve([source])[source.Identity]);
    }

    [Fact]
    public void TwoLiveApplePackagesCannotBeDisambiguatedByTheSameAumid()
    {
        var first = Source(ApplePath);
        var second = Source(ApplePath.Replace("1.2.3.4", "2.3.4.5"));
        var states = Snapshot(FavoritePlaybackSnapshot.AppleMusicAppId, FavoritePlaybackState.Playing).Resolve([first, second]);
        Assert.All(states.Values, state => Assert.Equal(FavoritePlaybackState.Unknown, state));
    }

    [Fact]
    public void SpotifyBareExecutableIdMatchesOneDistinctApplicationKey()
    {
        var spotify = Source(SpotifyPath);
        var apple = Source(ApplePath);
        var states = Snapshot("SPOTIFY.EXE", FavoritePlaybackState.Playing).Resolve([spotify, apple]);
        Assert.Equal(FavoritePlaybackState.Playing, states[spotify.Identity]);
        Assert.Equal(FavoritePlaybackState.Unknown, states[apple.Identity]);
    }

    [Fact]
    public void DuplicateRowsOfOneAppDoNotMakeTheExecutableAmbiguous()
    {
        var source = Source(SpotifyPath);
        Assert.Equal(FavoritePlaybackState.Playing,
            Snapshot("Spotify.exe", FavoritePlaybackState.Playing).Resolve([source, source with { SessionToken = "other-session" }])[source.Identity]);
    }

    [Fact]
    public void TheSameExecutableNameAtTwoPathsRemainsUnknown()
    {
        var first = Source(SpotifyPath);
        var second = Source(@"D:\Other\Spotify.exe");
        var states = Snapshot("Spotify.exe", FavoritePlaybackState.Playing).Resolve([first, second]);
        Assert.All(states.Values, state => Assert.Equal(FavoritePlaybackState.Unknown, state));
    }

    [Theory]
    [InlineData("spotify")]
    [InlineData("Spotify")]
    [InlineData(" Spotify.exe")]
    [InlineData("Spotify.exe ")]
    [InlineData("prefixSpotify.exe")]
    [InlineData("Spotify.exe!App")]
    [InlineData(@"C:\Apps\Spotify.exe")]
    [InlineData("https://example.test/Spotify.exe")]
    [InlineData("Spotify.exe\n")]
    public void ExecutableIdDoesNotUseSubstringOrPathGuesses(string appId)
    {
        var source = Source(SpotifyPath);
        Assert.Equal(FavoritePlaybackState.Unknown,
            Snapshot(appId, FavoritePlaybackState.Playing).Resolve([source])[source.Identity]);
    }

    [Fact]
    public void GenericExecutableIdsWorkWithoutHardCodedApplicationNames()
    {
        var source = Source(@"D:\Music\foobar2000.exe");
        Assert.Equal(FavoritePlaybackState.Playing,
            Snapshot("foobar2000.exe", FavoritePlaybackState.Playing).Resolve([source])[source.Identity]);
    }

    [Fact]
    public void ProtectedProcessesWithoutAStablePathRemainUnknown()
    {
        var source = Source(SpotifyPath) with { CanPersist = false, ExecutablePath = null };
        Assert.Equal(FavoritePlaybackState.Unknown,
            Snapshot("Spotify.exe", FavoritePlaybackState.Playing).Resolve([source])[source.Identity]);
    }

    [Fact]
    public void MissingSessionsLeaveExplicitUnknownValuesForEverySource()
    {
        var sources = new[] { Source(ApplePath), Source(SpotifyPath) };
        var states = new FavoritePlaybackSnapshot(7, []).Resolve(sources);
        Assert.Equal(2, states.Count);
        Assert.All(states.Values, state => Assert.Equal(FavoritePlaybackState.Unknown, state));
    }

    [Theory]
    [InlineData(FavoritePlaybackState.Playing, FavoritePlaybackState.NotPlaying)]
    [InlineData(FavoritePlaybackState.Playing, FavoritePlaybackState.Unknown)]
    [InlineData(FavoritePlaybackState.NotPlaying, FavoritePlaybackState.Unknown)]
    public void ConflictingMediaSessionsDoNotClaimAPlaybackState(FavoritePlaybackState first, FavoritePlaybackState second)
    {
        var source = Source(SpotifyPath);
        var snapshot = new FavoritePlaybackSnapshot(11, [new("Spotify.exe", first), new("Spotify.exe", second)]);
        Assert.Equal(FavoritePlaybackState.Unknown, snapshot.Resolve([source])[source.Identity]);
    }

    [Theory]
    [InlineData(FavoritePlaybackState.Playing)]
    [InlineData(FavoritePlaybackState.NotPlaying)]
    public void AgreeingMediaSessionsRetainTheirState(FavoritePlaybackState state)
    {
        var source = Source(SpotifyPath);
        var snapshot = new FavoritePlaybackSnapshot(11, [new("Spotify.exe", state), new("Spotify.exe", state)]);
        Assert.Equal(state, snapshot.Resolve([source])[source.Identity]);
    }

    [Fact]
    public void MutatingTheInputAfterPublicationCannotAlterTheSnapshot()
    {
        var source = Source(SpotifyPath);
        var input = new List<FavoritePlaybackSession> { new("Spotify.exe", FavoritePlaybackState.Playing) };
        var snapshot = new FavoritePlaybackSnapshot(21, input);
        input[0] = new("Spotify.exe", FavoritePlaybackState.NotPlaying);
        Assert.Equal(21, snapshot.Version);
        Assert.Equal(FavoritePlaybackState.Playing, snapshot.Resolve([source])[source.Identity]);
    }

    [Fact]
    public void ATruncatedSessionCensusCannotClaimUniqueAssociations()
    {
        var source = Source(SpotifyPath);
        var sessions = Enumerable.Range(0, FavoritePlaybackMonitor.MaximumSessions + 1)
            .Select(_ => new FavoritePlaybackSession("Spotify.exe", FavoritePlaybackState.Playing));
        Assert.Equal(FavoritePlaybackState.Unknown,
            new FavoritePlaybackSnapshot(30, sessions).Resolve([source])[source.Identity]);
    }

    [Theory]
    [InlineData(ApplePath)]
    [InlineData(SpotifyPath)]
    public void FavoriteMusicAppsNeverFallBackToWindowsActivityWhenMediaStatusIsMissing(string path)
    {
        var source = Source(path);
        Assert.Equal(FavoritePlaybackState.Unknown,
            new FavoritePlaybackSnapshot(1, []).ResolveForFavorites([source])[source.Identity]);
    }

    [Fact]
    public void GenericAppWithoutAMediaSessionCanUseWindowsActivityForFavoritesOnly()
    {
        var source = Source(@"D:\Games\game.exe");
        var snapshot = new FavoritePlaybackSnapshot(1, [new("Other.exe", FavoritePlaybackState.Unknown)]);
        Assert.Equal(FavoritePlaybackState.Unknown, snapshot.Resolve([source])[source.Identity]);
        Assert.Equal(FavoritePlaybackState.Playing, snapshot.ResolveForFavorites([source])[source.Identity]);
    }

    [Fact]
    public void GenericAppWithChangingMediaStateCannotFallBackToWindowsActivity()
    {
        var source = Source(@"D:\Music\foobar2000.exe");
        Assert.Equal(FavoritePlaybackState.Unknown,
            Snapshot("foobar2000.exe", FavoritePlaybackState.Unknown).ResolveForFavorites([source])[source.Identity]);
    }

    [Fact]
    public void AmbiguousGenericExecutableAssociationCannotFallBackToWindowsActivity()
    {
        var sources = new[] { Source(@"D:\Music\player.exe"), Source(@"E:\Other\player.exe") };
        var states = Snapshot("player.exe", FavoritePlaybackState.Playing).ResolveForFavorites(sources);
        Assert.All(states.Values, state => Assert.Equal(FavoritePlaybackState.Unknown, state));
    }

    [Fact]
    public void ConflictingGenericMediaSessionsCannotFallBackToWindowsActivity()
    {
        var source = Source(@"D:\Music\player.exe");
        var snapshot = new FavoritePlaybackSnapshot(1,
            [new("player.exe", FavoritePlaybackState.Playing), new("player.exe", FavoritePlaybackState.NotPlaying)]);
        Assert.Equal(FavoritePlaybackState.Unknown, snapshot.ResolveForFavorites([source])[source.Identity]);
    }

    [Fact]
    public void AnIncompleteMediaCensusCannotFallBackToWindowsActivity()
    {
        var source = Source(@"D:\Games\game.exe");
        var sessions = Enumerable.Range(0, FavoritePlaybackMonitor.MaximumSessions + 1)
            .Select(_ => new FavoritePlaybackSession("Other.exe", FavoritePlaybackState.Playing));
        Assert.Equal(FavoritePlaybackState.Unknown,
            new FavoritePlaybackSnapshot(1, sessions).ResolveForFavorites([source])[source.Identity]);
    }

    [Fact]
    public void AFailedMediaCensusCannotFallBackToWindowsActivity()
    {
        var source = Source(@"D:\Games\game.exe");
        Assert.Equal(FavoritePlaybackState.Unknown,
            new FavoritePlaybackSnapshot(1, [], false).ResolveForFavorites([source])[source.Identity]);
    }

    [Theory]
    [InlineData(ApplePath)]
    [InlineData(SpotifyPath)]
    [InlineData(@"D:\Music\player.exe")]
    public void WindowsInactiveSourceCannotRemainPlayingThroughMediaEvidence(string path)
    {
        var source = Source(path) with { IsActive = false };
        var id = source.SupportsAutomaticRate ? FavoritePlaybackSnapshot.AppleMusicAppId : Path.GetFileName(path);
        Assert.Equal(FavoritePlaybackState.NotPlaying,
            Snapshot(id, FavoritePlaybackState.Playing).ResolveForFavorites([source])[source.Identity]);
    }

    [Fact]
    public void DuplicateIdentitiesAcrossEndpointsUseTheirCombinedWindowsActivity()
    {
        var active = Source(@"D:\Games\game.exe");
        var inactive = active with { IsActive = false };
        Assert.Equal(FavoritePlaybackState.Playing,
            new FavoritePlaybackSnapshot(1, []).ResolveForFavorites([inactive, active])[active.Identity]);
    }

    [Fact]
    public async Task DisposalWithoutStartupIsIdempotentAndInvalidatesEvidence()
    {
        var monitor = new FavoritePlaybackMonitor();
        var version = monitor.Snapshot.Version;
        Assert.True(monitor.IsCurrent(version));
        await monitor.DisposeAsync();
        await monitor.DisposeAsync();
        Assert.False(monitor.IsCurrent(version));
        Assert.Empty(monitor.Snapshot.Resolve([]));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => monitor.StartAsync());
    }

    [Fact]
    public async Task CancelledStartupDoesNotTouchTheWindowsMediaManager()
    {
        await using var monitor = new FavoritePlaybackMonitor();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => monitor.StartAsync(cancellation.Token));
        Assert.Equal(0, monitor.Snapshot.Version);
    }

    private static FavoritePlaybackSnapshot Snapshot(string id, FavoritePlaybackState state) => new(1, [new(id, state)]);
    private static PlatformAudioSource Source(string path) => new(
        path.ToUpperInvariant(), Path.GetFileNameWithoutExtension(path), path, [123], true, true, "session");
}

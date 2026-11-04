using WindowsLosslessSwitcher.Models;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class CatalogObservationGuardTests
{
    private const string PackageDirectory = @"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.2.3.4_x64__nzyj5cx40ttqa";

    [Theory]
    [InlineData("AppleMusic.exe")]
    [InlineData("AMPLibraryAgent.exe")]
    public void KnownPackagePathAndExecutablePermitOnlyTheOptionalCatalogAdapter(string fileName)
    {
        Assert.True(PlatformAudioSource.IsOfficialAppleMusicExecutable(PackageDirectory + "\\" + fileName));
        Assert.True(PlatformAudioSource.IsOfficialAppleMusicExecutable((PackageDirectory + "\\" + fileName).ToUpperInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Apps\AppleMusic.exe")]
    [InlineData(@"C:\Apps\AMPLibraryAgent.exe")]
    [InlineData(@"C:\Apps\AppleMusicWin_fake\AppleMusic.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.2.3.4_x64__wrongpublisher\AppleMusic.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.2.3.4_x64__nzyj5cx40ttqa\game.exe")]
    public void AProcessNameAloneOrAnotherPackageIsInsufficient(string? path)
        => Assert.False(PlatformAudioSource.IsOfficialAppleMusicExecutable(path));

    [Fact]
    public void MatchingPlayingMetadataCanSupportACatalogEstimate()
        => Assert.True(AutomaticRateMonitor.CanUseCatalogTrack(Source(), Track(), Session()));

    [Theory]
    [InlineData(MediaTransportPlaybackStatus.Playing)]
    [InlineData(MediaTransportPlaybackStatus.Paused)]
    public void MatchingTrackMetadataCanRemainVisibleWithoutAnActiveWindowsStream(MediaTransportPlaybackStatus status)
    {
        var source = Source() with { IsActive = false };
        var session = Session() with { PlaybackStatus = status };

        Assert.True(AutomaticRateMonitor.CanUseCatalogMetadataTrack(source, Track(), session));
        Assert.False(AutomaticRateMonitor.CanUseCatalogTrack(source, Track(), session));
    }

    [Theory]
    [InlineData(MediaTransportPlaybackStatus.Unknown)]
    [InlineData(MediaTransportPlaybackStatus.Closed)]
    [InlineData(MediaTransportPlaybackStatus.Stopped)]
    [InlineData(MediaTransportPlaybackStatus.Changing)]
    public void UnconfirmedOrClosedPlaybackCannotKeepTrackMetadataCurrent(MediaTransportPlaybackStatus status)
        => Assert.False(AutomaticRateMonitor.CanUseCatalogMetadataTrack(Source(), Track(), Session() with { PlaybackStatus = status }));

    [Fact]
    public void PausedMetadataStillRequiresTheSameTrackAndApplication()
    {
        var paused = Session() with { PlaybackStatus = MediaTransportPlaybackStatus.Paused };
        Assert.True(AutomaticRateMonitor.CanUseCatalogMetadataTrack(Source(), Track(), paused));
        Assert.False(AutomaticRateMonitor.CanUseCatalogMetadataTrack(Source(), Track(), paused with { Title = "Different track" }));
        Assert.False(AutomaticRateMonitor.CanUseCatalogMetadataTrack(Source(), Track(), paused with { SourceAppUserModelId = "another-app" }));
    }

    [Theory]
    [InlineData(MediaTransportPlaybackStatus.Unknown)]
    [InlineData(MediaTransportPlaybackStatus.Closed)]
    [InlineData(MediaTransportPlaybackStatus.Stopped)]
    [InlineData(MediaTransportPlaybackStatus.Changing)]
    [InlineData(MediaTransportPlaybackStatus.Paused)]
    public void NonPlayingMediaSessionMustHoldEvenWhenWindowsAudioSessionRemainsActive(MediaTransportPlaybackStatus status)
        => Assert.False(AutomaticRateMonitor.CanUseCatalogTrack(Source(), Track(), Session() with { PlaybackStatus = status }));

    [Fact]
    public void DetachedMediaSessionCannotReusePreviousTrackAfterAudioProcessRestarts()
    {
        var restarted = Source() with { ProcessIds = [9876], SessionToken = "restarted-session" };
        Assert.False(AutomaticRateMonitor.CanUseCatalogTrack(restarted, Track(), MediaSessionSnapshot.CreateUnavailable()));
    }

    [Fact]
    public void NewlyReportedTrackWaitsUntilMediaSessionMetadataCatchesUp()
    {
        var changed = Track() with { Title = "Next track" };
        Assert.False(AutomaticRateMonitor.CanUseCatalogTrack(Source(), changed, Session()));
        Assert.True(AutomaticRateMonitor.CanUseCatalogTrack(Source(), changed, Session() with { Title = "Next track" }));
    }

    [Fact]
    public void EveryMetadataIdentityFieldMustMatch()
    {
        var session = Session();
        Assert.False(AutomaticRateMonitor.CanUseCatalogTrack(Source(), Track(), session with { SourceAppUserModelId = "other-app" }));
        Assert.False(AutomaticRateMonitor.CanUseCatalogTrack(Source(), Track(), session with { Title = "other-title" }));
        Assert.False(AutomaticRateMonitor.CanUseCatalogTrack(Source(), Track(), session with { Artist = "other-artist" }));
        Assert.False(AutomaticRateMonitor.CanUseCatalogTrack(Source(), Track(), session with { Album = "other-album" }));
    }

    [Fact]
    public void IdleMissingAndUnrecognizedSourceCannotUseCatalogMetadata()
    {
        Assert.False(AutomaticRateMonitor.CanUseCatalogTrack(Source() with { IsActive = false }, Track(), Session()));
        Assert.False(AutomaticRateMonitor.CanUseCatalogTrack(null, Track(), Session()));
        Assert.False(AutomaticRateMonitor.CanUseCatalogTrack(Source(), null, Session()));
        Assert.False(AutomaticRateMonitor.CanUseCatalogTrack(Source() with { ExecutablePath = @"C:\Apps\AppleMusic.exe" }, Track(), Session()));
    }

    private static PlatformAudioSource Source() => new("APPLE-MUSIC-KEY", "AppleMusic",
        PackageDirectory + @"\AppleMusic.exe", [123], true, true, "current-session");

    private static TrackSnapshot Track() => new("AppleInc.AppleMusicWin!App", null, "Song", "Artist", "Album", "test", DateTimeOffset.UtcNow);

    private static MediaSessionSnapshot Session() => MediaSessionSnapshot.CreateUnavailable() with
    {
        SourceAppUserModelId = "AppleInc.AppleMusicWin!App",
        Title = "Song", Artist = "Artist", Album = "Album",
        PlaybackStatus = MediaTransportPlaybackStatus.Playing,
    };
}

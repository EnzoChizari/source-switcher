using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class AppleMusicTransitionGuardTests
{
    private const string Apple = @"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.1540.23042.0_x64__nzyj5cx40ttqa\AppleMusic.exe";
    private static PlatformAudioSource Source(bool active) => new(Apple, "Apple Music", Apple, [1], active, true, "session");

    [Fact]
    public void PlayingMasterGetsOneBoundedGraceWhileItsStreamRestarts()
    {
        var guard = new AppleMusicTransitionGuard();
        Assert.Equal(TimeSpan.FromSeconds(1), guard.RetryAfter(Source(false), FavoritePlaybackState.Playing, TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromMilliseconds(100), guard.RetryAfter(Source(false), FavoritePlaybackState.Playing, TimeSpan.FromMilliseconds(900)));
        Assert.Null(guard.RetryAfter(Source(false), FavoritePlaybackState.Playing, TimeSpan.FromSeconds(1)));
        Assert.Null(guard.RetryAfter(Source(false), FavoritePlaybackState.Playing, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void PauseAndStreamReturnImmediatelyEndTheGrace()
    {
        var guard = new AppleMusicTransitionGuard();
        guard.RetryAfter(Source(false), FavoritePlaybackState.Playing, TimeSpan.Zero);
        Assert.Null(guard.RetryAfter(Source(false), FavoritePlaybackState.NotPlaying, TimeSpan.FromMilliseconds(100)));
        Assert.Null(guard.RetryAfter(Source(true), FavoritePlaybackState.Playing, TimeSpan.FromMilliseconds(200)));
        Assert.Equal(TimeSpan.FromSeconds(1), guard.RetryAfter(Source(false), FavoritePlaybackState.Playing, TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void AnotherMasterAndUncertainPlaybackDoNotReceiveTheGrace()
    {
        var guard = new AppleMusicTransitionGuard();
        var other = Source(false) with { Identity = @"C:\Apps\Discord.exe", ExecutablePath = @"C:\Apps\Discord.exe" };
        Assert.Null(guard.RetryAfter(other, FavoritePlaybackState.Playing, TimeSpan.Zero));
        Assert.Null(guard.RetryAfter(Source(false), FavoritePlaybackState.Unknown, TimeSpan.Zero));
        Assert.Null(guard.RetryAfter(null, FavoritePlaybackState.Playing, TimeSpan.Zero));
    }
}

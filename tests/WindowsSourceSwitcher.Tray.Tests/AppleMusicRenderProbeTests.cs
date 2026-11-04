using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;
namespace WindowsSourceSwitcher.Tray.Tests;
public sealed class AppleMusicRenderProbeTests
{
    private const string Apple = @"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.2.3.4_x64__nzyj5cx40ttqa\AppleMusic.exe";
    private const string Agent = @"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.2.3.4_x64__nzyj5cx40ttqa\AMPLibraryAgent.exe";
    private static VolumeSessionIdentity Identity(string path, bool shared = false) => new("endpoint", SourceIdentity.FromExecutablePath(path), 123,
        DateTimeOffset.UnixEpoch, path, "session", false, shared);
    private static AudioSessionVolumeSnapshot Source(VolumeSessionIdentity identity, bool active = true, string? error = null) =>
        new("endpoint", identity.SourceIdentity, "name", 1, false, false, false, [identity], false, active, error);
    [Fact]
    public void SelectsOnlyIdentifiedCurrentAppleSessionsFromSamePackage()
    {
        var valid = Identity(Agent); var unrelated = Identity(@"C:\Apps\player.exe");
        var foreign = Identity(Agent.Replace("1.2.3.4", "1.2.3.5"));
        var rows = new[] { Source(valid), Source(unrelated), Source(foreign), Source(valid with { ProcessStartedAtUtc = null }),
            Source(valid with { ProcessId = 0 }), Source(valid, false) };
        Assert.Equal(valid, Assert.Single(AppleMusicRenderProbe.SelectSessions(new("endpoint", rows, null), SourceIdentity.FromExecutablePath(Apple))));
    }
    [Fact]
    public void SharedAppleCreatorIsSupportingEvidenceButRemainsReadonlyInMixer()
    {
        var shared = Identity(Agent, true);
        var selected = AppleMusicRenderProbe.SelectSessions(new("endpoint", [Source(shared, error: "Shared session is readonly")], null), SourceIdentity.FromExecutablePath(Apple));
        Assert.Equal(shared, Assert.Single(selected));
        Assert.False(AudioSessionMeterMonitor.CanObserve(shared, "endpoint", shared.SourceIdentity));
        Assert.True(AudioSessionMeterMonitor.CanObserve(shared, "endpoint", shared.SourceIdentity, allowSharedSessions: true));
        Assert.False(AudioSessionMeterMonitor.CanObserve(shared with { ProcessStartedAtUtc = null }, "endpoint", shared.SourceIdentity, allowSharedSessions: true));
    }
}

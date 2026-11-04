using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class AppleMusicSourceGroupingTests
{
    private const string Package = @"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.1540.23042.0_x64__nzyj5cx40ttqa";
    private const string Main = Package + @"\AppleMusic.exe";
    private const string Helper = Package + @"\AMPLibraryAgent.exe";

    [Fact]
    public void PlaybackHelperAloneBecomesAnActiveAppleMusicSourceWithRealOwnerPaths()
    {
        var source = Assert.Single(AudioSessionEnumerator.GroupRows("dac", [Row(Helper, 20, true, "helper-1"), Row(Helper, 20, true, "helper-2")]));
        Assert.Equal(SourceIdentity.FromExecutablePath(Main), source.Identity);
        Assert.Equal("Apple Music", source.ProcessName);
        Assert.True(source.IsActive);
        Assert.True(source.SupportsAutomaticRate);
        Assert.Equal(new[] { 20 }, source.ProcessIds);
        Assert.Equal(Helper, source.ProcessExecutablePaths[20]);
        Assert.Equal(new[] { "helper-1", "helper-2" }, source.SessionInstanceIds);
    }

    [Fact]
    public void IdleUiAndPlayingHelperMergeWithoutLosingSessionMembership()
    {
        var source = Assert.Single(AudioSessionEnumerator.GroupRows("dac", [Row(Main, 10, false, "ui"), Row(Helper, 20, true, "helper")]));
        Assert.True(source.IsActive);
        Assert.Equal(new[] { 10, 20 }, source.ProcessIds);
        Assert.Equal(Main, source.ProcessExecutablePaths[10]);
        Assert.Equal(Helper, source.ProcessExecutablePaths[20]);
        Assert.Equal(2, source.SessionInstanceIds.Count);
    }

    [Fact]
    public void HelperRestartPreservesLogicalIdentityAndInvalidatesSessionEvidence()
    {
        var before = Assert.Single(AudioSessionEnumerator.GroupRows("dac", [Row(Helper, 20, true, "old")]));
        var after = Assert.Single(AudioSessionEnumerator.GroupRows("dac", [Row(Helper, 21, true, "new")]));
        Assert.Equal(before.Identity, after.Identity);
        Assert.NotEqual(before.SessionToken, after.SessionToken);
    }

    [Theory]
    [InlineData(@"C:\Apps\AMPLibraryAgent.exe")]
    [InlineData(@"D:\WindowsApps\AppleInc.AppleMusicWin_1.1540.23042.0_x64__nzyj5cx40ttqa\AMPLibraryAgent.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.1540.23043.0_x64__nzyj5cx40ttqa\AMPLibraryAgent.exe")]
    public void UnrelatedProcessesAndOtherPackageVersionsNeverMerge(string path) =>
        Assert.Equal(2, AudioSessionEnumerator.GroupRows("dac", [Row(Main, 10, false, "ui"), Row(path, 20, true, "other")]).Count);

    private static AudioSessionEnumerator.SessionRow Row(string path, int pid, bool active, string instance) =>
        new(SourceIdentity.FromExecutablePath(path), Path.GetFileNameWithoutExtension(path), path, pid, active, instance, instance);
}

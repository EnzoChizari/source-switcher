using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Core.Tests;

public sealed class AppleMusicIdentityTests
{
    private const string Package = @"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.1540.23042.0_x64__nzyj5cx40ttqa";

    [Theory]
    [InlineData("AppleMusic.exe")]
    [InlineData("AMPLibraryAgent.exe")]
    [InlineData("amplibraryagent.EXE")]
    public void BothKnownPackageRolesHaveOneVersionBoundLogicalIdentity(string executable)
    {
        var path = Package + "\\" + executable;
        Assert.True(AppleMusicIdentity.IsOfficialExecutable(path));
        Assert.Equal((Package + "\\AppleMusic.exe").ToUpperInvariant(), AppleMusicIdentity.CanonicalPath(path));
        Assert.True(AppleMusicIdentity.Equivalent(path, Package + "\\AppleMusic.exe"));
    }

    [Theory]
    [InlineData(@"D:\WindowsApps\AppleInc.AppleMusicWin_1.2.3.4_x64__nzyj5cx40ttqa\AMPLibraryAgent.exe")]
    [InlineData(@"D:/WindowsApps/AppleInc.AppleMusicWin_1.2.3.4_arm64__nzyj5cx40ttqa/AppleMusic.exe")]
    [InlineData(@"C:\Program Files (x86)\WindowsApps\AppleInc.AppleMusicWin_1.2.3.4_x86__nzyj5cx40ttqa\AppleMusic.exe")]
    public void SupportedStorePackageRootsAndWindowsSeparatorsAreRecognized(string path)
    {
        Assert.True(AppleMusicIdentity.IsOfficialExecutable(path));
        Assert.EndsWith("\\APPLEMUSIC.EXE", AppleMusicIdentity.CanonicalPath(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AppleMusic.exe")]
    [InlineData(@"C:\Apps\AppleMusic.exe")]
    [InlineData(@"C:\Games\WindowsApps\AppleInc.AppleMusicWin_1.2.3.4_x64__nzyj5cx40ttqa\AppleMusic.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.2.3.4_x64__nzyj5cx40ttqa\Nested\AppleMusic.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.2.3.4_x64__nzyj5cx40ttqa\Other.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.2.3.4_x64__wrong\AppleMusic.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Other.AppleMusicWin_1.2.3.4_x64__nzyj5cx40ttqa\AppleMusic.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.2.3_x64__nzyj5cx40ttqa\AppleMusic.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.2.3.999999_x64__nzyj5cx40ttqa\AppleMusic.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.2.3.4_unknown__nzyj5cx40ttqa\AppleMusic.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.2.3.4_x64__nzyj5cx40ttqa\..\AppleMusic.exe")]
    [InlineData(@"\\server\WindowsApps\AppleInc.AppleMusicWin_1.2.3.4_x64__nzyj5cx40ttqa\AppleMusic.exe")]
    [InlineData(@"\\?\C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.2.3.4_x64__nzyj5cx40ttqa\AppleMusic.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.2.3.4_x64__nzyj5cx40ttqa\AppleMusic.exe:other")]
    public void UnrelatedMalformedOrNestedExecutablesAreNotAliased(string? path)
    {
        Assert.False(AppleMusicIdentity.IsOfficialExecutable(path));
        Assert.Equal(path, AppleMusicIdentity.CanonicalPath(path));
        Assert.False(AppleMusicIdentity.Equivalent(path, path));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("drive")]
    [InlineData("architecture")]
    public void OtherPackageInstallationsCannotInheritTheLogicalIdentity(string difference)
    {
        var left = Package + "\\AppleMusic.exe";
        var right = difference switch
        {
            "version" => left.Replace("1.1540.23042.0", "1.1540.23043.0", StringComparison.Ordinal),
            "drive" => "D:" + left[2..],
            _ => left.Replace("_x64__", "_arm64__", StringComparison.Ordinal)
        };
        Assert.True(AppleMusicIdentity.IsOfficialExecutable(right));
        Assert.False(AppleMusicIdentity.Equivalent(left, right));
    }

    [Fact]
    public void CaseAndSlashDifferencesDoNotSplitOneActualPackage()
    {
        Assert.True(AppleMusicIdentity.Equivalent(Package + "\\AppleMusic.exe",
            (Package + "\\AMPLibraryAgent.exe").ToLowerInvariant().Replace('\\', '/')));
    }
}

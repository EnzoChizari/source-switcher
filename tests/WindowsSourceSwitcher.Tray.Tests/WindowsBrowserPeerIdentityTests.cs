using System.ComponentModel;
using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using WindowsSourceSwitcher.BrowserBridge;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class WindowsBrowserPeerIdentityTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private const string HostPath = @"C:\Audio\browser-helper\WindowsSourceSwitcher.BrowserHost.exe";
    private const string TrayPath = @"C:\Audio\WindowsSourceSwitcher.Tray.exe";
    private const string BrowserPath = @"C:\Program Files\Zen Browser\zen.exe";
    private static readonly DateTimeOffset Started = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SecurityDescriptorHasOnlyExplicitUserAllowAndMediumMandatoryLabel()
    {
        var descriptor = WindowsBrowserPeerIdentity.BuildPipeSecurityDescriptor(Sid);
        Assert.Equal($"D:P(A;;GA;;;{Sid})S:(ML;;NW;;;ME)", descriptor);
        Assert.DoesNotContain(";;;WD", descriptor);
        Assert.DoesNotContain(";;;AN", descriptor);
        Assert.DoesNotContain(";;;BA", descriptor);
    }

    [Fact]
    public void SecurityDescriptorDoesNotAcceptSddlAsUserIdentity() =>
        Assert.ThrowsAny<ArgumentException>(() => WindowsBrowserPeerIdentity.BuildPipeSecurityDescriptor("S-1-5-21-1)(A;;GA;;;WD)"));

    [Theory]
    [InlineData(@"C:/Audio/browser-helper/WindowsSourceSwitcher.BrowserHost.exe")]
    [InlineData(@"c:\audio\BROWSER-HELPER\WINDOWSSOURCESWITCHER.BROWSERHOST.EXE")]
    [InlineData(@"\\?\C:\Audio\browser-helper\WindowsSourceSwitcher.BrowserHost.exe")]
    public void ExecutableIdentityNormalizesWindowsCaseAndSeparators(string path) =>
        Assert.Equal(HostPath.ToUpperInvariant(), WindowsBrowserPeerIdentity.NormalizeExecutablePath(path));

    [Theory]
    [InlineData("host.exe")]
    [InlineData(@"C:host.exe")]
    [InlineData(@"\\server\share\host.exe")]
    [InlineData(@"\\.\pipe\host.exe")]
    [InlineData(@"C:\Audio\host.exe:stream.exe")]
    [InlineData(@"C:\Audio\host.dll")]
    [InlineData(" C:\\Audio\\host.exe")]
    [InlineData("C:\\Audio\\host.exe\r\n")]
    public void ExecutableIdentityRejectsRelativeRemoteDeviceAndAlternateStreamPaths(string path) =>
        Assert.ThrowsAny<ArgumentException>(() => WindowsBrowserPeerIdentity.NormalizeExecutablePath(path));

    [Theory]
    [InlineData("normal.pipe-1_2")]
    [InlineData("DX5SourceSwitcher.Browser.AABBCC001122")]
    public void LocalPipeNameAcceptsOnlyBoundedIdentifiers(string pipeName) =>
        WindowsBrowserPeerIdentity.ValidatePipeName(pipeName);

    [Theory]
    [InlineData(@"\\.\pipe\other")]
    [InlineData("../other")]
    [InlineData("name:stream")]
    [InlineData("name with spaces")]
    [InlineData("*")]
    public void LocalPipeNameRejectsNamespaceOrPathInputs(string pipeName) =>
        Assert.ThrowsAny<ArgumentException>(() => WindowsBrowserPeerIdentity.ValidatePipeName(pipeName));

    [Fact]
    public void LocalPipeNameRejectsUnboundedNames() =>
        Assert.ThrowsAny<ArgumentException>(() => WindowsBrowserPeerIdentity.ValidatePipeName(new string('a', 129)));

    [Fact]
    public void ExpectedPeerMustUseExactPublishedHelperSubfolderInBothDirections()
    {
        WindowsBrowserPeerIdentity.RequireInstalledPeerPath(TrayPath, HostPath,
            WindowsBrowserPeerIdentity.HostExecutableName);
        WindowsBrowserPeerIdentity.RequireInstalledPeerPath(HostPath, TrayPath,
            WindowsBrowserPeerIdentity.TrayExecutableName);
        Assert.Equal(TrayPath.ToUpperInvariant(), WindowsBrowserPeerIdentity.ExpectedTrayPathForHost(HostPath).ToUpperInvariant());
        Assert.Throws<UnauthorizedAccessException>(() => WindowsBrowserPeerIdentity.RequireInstalledPeerPath(
            TrayPath, @"D:\Other\WindowsSourceSwitcher.BrowserHost.exe", WindowsBrowserPeerIdentity.HostExecutableName));
        Assert.Throws<UnauthorizedAccessException>(() => WindowsBrowserPeerIdentity.RequireInstalledPeerPath(
            TrayPath, @"C:\Audio\renamed.exe", WindowsBrowserPeerIdentity.HostExecutableName));
    }

    [Theory]
    [InlineData(@"C:\Audio\WindowsSourceSwitcher.BrowserHost.exe")]
    [InlineData(@"C:\Audio\other-helper\WindowsSourceSwitcher.BrowserHost.exe")]
    [InlineData(@"C:\Other\browser-helper\WindowsSourceSwitcher.BrowserHost.exe")]
    public void HelperCannotBeAdjacentToTrayOrFromAnotherInstallation(string host) =>
        Assert.Throws<UnauthorizedAccessException>(() => WindowsBrowserPeerIdentity.RequireInstalledPeerPath(
            TrayPath, host, WindowsBrowserPeerIdentity.HostExecutableName));

    [Theory]
    [InlineData(@"C:\Audio\browser-helper\WindowsSourceSwitcher.Tray.exe")]
    [InlineData(@"C:\Other\WindowsSourceSwitcher.Tray.exe")]
    public void HelperCannotTrustATrayInItsOwnSubfolderOrAnotherInstallation(string tray) =>
        Assert.Throws<UnauthorizedAccessException>(() => WindowsBrowserPeerIdentity.RequireInstalledPeerPath(
            HostPath, tray, WindowsBrowserPeerIdentity.TrayExecutableName));

    [Fact]
    public void HostCannotInferATrayParentFromAnUnrecognizedHelperFolder() =>
        Assert.Throws<UnauthorizedAccessException>(() => WindowsBrowserPeerIdentity.ExpectedTrayPathForHost(
            @"C:\Audio\other\WindowsSourceSwitcher.BrowserHost.exe"));

    [Fact]
    public void PeerMustMatchActualUserSessionAndExpectedImage()
    {
        var tray = Process(1, TrayPath);
        var host = Process(2, HostPath);
        WindowsBrowserPeerIdentity.ValidateExpectedPeer(host, tray, HostPath.ToLowerInvariant());
        Assert.Throws<UnauthorizedAccessException>(() => WindowsBrowserPeerIdentity.ValidateExpectedPeer(
            host with { UserSid = "S-1-5-21-9-8-7-1001" }, tray, HostPath));
        Assert.Throws<UnauthorizedAccessException>(() => WindowsBrowserPeerIdentity.ValidateExpectedPeer(
            host with { SessionId = 2 }, tray, HostPath));
        Assert.Throws<UnauthorizedAccessException>(() => WindowsBrowserPeerIdentity.ValidateExpectedPeer(
            host with { ExecutablePath = @"C:\Other\WindowsSourceSwitcher.BrowserHost.exe" }, tray, HostPath));
        Assert.Throws<UnauthorizedAccessException>(() => WindowsBrowserPeerIdentity.ValidateExpectedPeer(
            host with { ProcessId = 0 }, tray, HostPath));
    }

    [Theory]
    [InlineData("zen.exe", "Zen", "zen.exe", "Mozilla Corporation", true)]
    [InlineData("zen.exe", "Zen Browser", "zen.exe", "Mozilla Corporation", true)]
    [InlineData("firefox.exe", "Firefox", "firefox.exe", "Mozilla Corporation", true)]
    [InlineData("firefox.exe", "Firefox Developer Edition", "firefox.exe", "Mozilla Corporation", true)]
    [InlineData("firefox.exe", "Nightly", "firefox.exe", "Mozilla Corporation", true)]
    [InlineData("firefox.exe", "Other app", "firefox.exe", "Mozilla Corporation", false)]
    [InlineData("zen.exe", "Zen", "renamed.exe", "Mozilla Corporation", false)]
    [InlineData("zen.exe", "Zen", "zen.exe", "Unknown", false)]
    [InlineData("chrome.exe", "Firefox", "chrome.exe", "Mozilla Corporation", false)]
    public void GeckoIdentityNeedsExpectedProductAndOriginalImageMetadata(string executable, string product,
        string original, string company, bool accepted) =>
        Assert.Equal(accepted, WindowsBrowserPeerIdentity.IsGeckoProduct(executable, product, original, company));

    [Theory]
    [InlineData("chrome.exe", "Helium", "chrome.exe", "The Helium Authors", true)]
    [InlineData("chrome.exe", "Google Chrome", "chrome.exe", "Google LLC", true)]
    [InlineData("chrome.exe", "Google Chrome", "chrome.exe", "Unknown", false)]
    [InlineData("chrome.exe", "Chromium", "chrome.exe", "The Chromium Authors", false)]
    [InlineData("chrome.exe", "Helium", "chrome.exe", "Unknown", false)]
    [InlineData("chrome.exe", "Helium", "other.exe", "The Helium Authors", false)]
    [InlineData("other.exe", "Helium", "chrome.exe", "The Helium Authors", false)]
    public void ChromiumProductMetadataDoesNotAdmitArbitraryChromeProcesses(string name, string product,
        string original, string company, bool accepted) =>
        Assert.Equal(accepted, WindowsBrowserPeerIdentity.IsSupportedBrowserProduct(name, product, original, company));

    [Fact]
    public void HeliumInstallRegistrationMustNameExactInstalledImage()
    {
        const string location = @"C:\Program Files\imput\Helium\Application";
        const string image = location + @"\chrome.exe";
        Assert.True(WindowsBrowserPeerIdentity.RegisteredHeliumInstallMatches("Helium", "The Helium Authors", location, image));
        Assert.False(WindowsBrowserPeerIdentity.RegisteredHeliumInstallMatches("Chrome", "The Helium Authors", location, image));
        Assert.False(WindowsBrowserPeerIdentity.RegisteredHeliumInstallMatches("Helium", "Unknown", location, image));
        Assert.False(WindowsBrowserPeerIdentity.RegisteredHeliumInstallMatches("Helium", "The Helium Authors", location, @"C:\Other\chrome.exe"));
        Assert.False(WindowsBrowserPeerIdentity.RegisteredHeliumInstallMatches("Helium", "The Helium Authors", null, image));
    }

    [Fact]
    public void RegistryPathIsComparedAsAnImageAndNeverAsAShellCommand()
    {
        Assert.True(WindowsBrowserPeerIdentity.RegistryPathEquals('"' + BrowserPath + '"', BrowserPath));
        Assert.True(WindowsBrowserPeerIdentity.RegistryCommandPathEquals('"' + BrowserPath + "\" -url \"%1\"", BrowserPath));
        Assert.False(WindowsBrowserPeerIdentity.RegistryPathEquals(BrowserPath + " --extra", BrowserPath));
        Assert.False(WindowsBrowserPeerIdentity.RegistryCommandPathEquals("cmd.exe /c \"" + BrowserPath + '"', BrowserPath));
        Assert.False(WindowsBrowserPeerIdentity.RegistryCommandPathEquals(BrowserPath + " --extra", BrowserPath));
        Assert.False(WindowsBrowserPeerIdentity.RegistryPathEquals(@"C:\Other\zen.exe", BrowserPath));
    }

    [Fact]
    public void BrowserParentIdentityIsDerivedLocallyAndUsesHighestSameImageAncestor()
    {
        var host = Process(10, HostPath, 10);
        var reader = BrowserTree(host);
        var result = WindowsBrowserPeerIdentity.FindBrowser(host, reader);
        Assert.Equal(BrowserPath.ToUpperInvariant(), result.BrowserExecutablePath);
        Assert.Equal(30, result.BrowserProcessId);
        Assert.Equal(Started, result.BrowserStartedAtUtc);
        Assert.Equal(host.ProcessId, result.HostProcessId);
        Assert.All(reader.ReadIds, id => Assert.Contains(id, new[] { 20, 30, 40 }));
    }

    [Fact]
    public void UnregisteredPortableOrRenamedBrowserCannotSupplyDecoderAuthority()
    {
        var host = Process(10, HostPath, 10);
        var reader = BrowserTree(host);
        reader.Registered.Clear();
        Assert.Throws<UnauthorizedAccessException>(() => WindowsBrowserPeerIdentity.FindBrowser(host, reader));
    }

    [Fact]
    public void HostStartedThroughShellCannotClaimABrowserFurtherUpItsAncestry()
    {
        var host = Process(10, HostPath, 10);
        var reader = BrowserTree(host);
        reader.Processes[20] = Process(20, @"C:\Windows\System32\cmd.exe", 5);
        Assert.Throws<UnauthorizedAccessException>(() => WindowsBrowserPeerIdentity.FindBrowser(host, reader));
    }

    [Fact]
    public void ActualHeliumNativeMessagingShellLayoutUsesRegisteredBrowserIdentity()
    {
        var host = Process(10, HostPath, 10);
        var reader = HeliumShellTree(host);
        var identity = WindowsBrowserPeerIdentity.FindBrowser(host, reader);
        Assert.Equal(30, identity.BrowserProcessId);
        Assert.Equal(@"C:\Program Files\imput\Helium\Application\chrome.exe".ToUpperInvariant(), identity.BrowserExecutablePath);
        Assert.Contains(20, reader.ReadIds); // Shell is checked again before accepting the tree.
        Assert.Equal(2, reader.ReadIds.Count(pid => pid == 20));
    }

    [Theory]
    [InlineData("arbitrary-shell")]
    [InlineData("second-shell")]
    [InlineData("unregistered-browser")]
    [InlineData("different-user")]
    [InlineData("different-session")]
    [InlineData("late-parent")]
    public void HeliumShellExceptionCannotBecomeGenericAncestorTrust(string mismatch)
    {
        var host = Process(10, HostPath, 10);
        var reader = HeliumShellTree(host);
        switch (mismatch)
        {
            case "arbitrary-shell": reader.Processes[20] = reader.Processes[20] with { ExecutablePath = @"C:\Tools\cmd.exe" }; break;
            case "second-shell": reader.Processes[30] = reader.Processes[30] with { ExecutablePath = Path.Combine(Environment.SystemDirectory, "cmd.exe") }; break;
            case "unregistered-browser": reader.Registered.Clear(); break;
            case "different-user": reader.Processes[20] = reader.Processes[20] with { UserSid = "S-1-5-21-2-3-4-1001" }; break;
            case "different-session": reader.Processes[20] = reader.Processes[20] with { SessionId = 5 }; break;
            default: reader.Processes[30] = reader.Processes[30] with { StartedAtUtc = host.StartedAtUtc.AddSeconds(1) }; break;
        }
        Assert.Throws<UnauthorizedAccessException>(() => WindowsBrowserPeerIdentity.FindBrowser(host, reader));
    }

    [Fact]
    public void ReusedNativeMessagingShellPidIsRejected()
    {
        var host = Process(10, HostPath, 10);
        var reader = HeliumShellTree(host);
        reader.AfterRead = (pid, reads) => pid == 20 && reads == 1
            ? reader.Processes[20] = reader.Processes[20] with { StartedAtUtc = Started.AddSeconds(20) }
            : null;
        Assert.Throws<UnauthorizedAccessException>(() => WindowsBrowserPeerIdentity.FindBrowser(host, reader));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("session")]
    [InlineData("parent-start")]
    public void HostParentMustHaveSameUserSessionAndPrecedeChildLifetime(string mismatch)
    {
        var host = Process(10, HostPath, 10);
        var reader = BrowserTree(host);
        reader.Processes[20] = mismatch switch
        {
            "user" => reader.Processes[20] with { UserSid = "S-1-5-21-2-3-4-1001" },
            "session" => reader.Processes[20] with { SessionId = 3 },
            _ => reader.Processes[20] with { StartedAtUtc = host.StartedAtUtc.AddSeconds(1) }
        };
        Assert.Throws<UnauthorizedAccessException>(() => WindowsBrowserPeerIdentity.FindBrowser(host, reader));
    }

    [Fact]
    public void BrowserAncestryRejectsCycles()
    {
        var host = Process(10, HostPath, 10);
        var reader = BrowserTree(host);
        reader.Parents[30] = 20;
        Assert.Throws<UnauthorizedAccessException>(() => WindowsBrowserPeerIdentity.FindBrowser(host, reader));
    }

    [Fact]
    public void BrowserAncestryIsBounded()
    {
        var host = Process(10, HostPath, 100);
        var reader = new FakeReader();
        reader.Parents[10] = 20;
        for (var pid = 20; pid < 50; pid++)
        {
            reader.Processes[pid] = Process(pid, BrowserPath, 80 - pid);
            reader.Parents[pid] = pid + 1;
        }
        reader.Registered.Add(BrowserPath.ToUpperInvariant());
        Assert.Throws<UnauthorizedAccessException>(() => WindowsBrowserPeerIdentity.FindBrowser(host, reader));
        Assert.InRange(reader.ReadIds.Count, 1, 17);
    }

    [Fact]
    public void BrowserPidReuseDuringValidationIsRejected()
    {
        var host = Process(10, HostPath, 10);
        var reader = BrowserTree(host);
        reader.AfterRead = (pid, reads) => pid == 20 && reads == 1
            ? reader.Processes[20] = reader.Processes[20] with { StartedAtUtc = Started.AddSeconds(20) }
            : null;
        Assert.Throws<UnauthorizedAccessException>(() => WindowsBrowserPeerIdentity.FindBrowser(host, reader));
    }

    [Fact]
    public void ExitedLaunchersOutsideBrowserTreeDoNotInventAnotherBrowserRoot()
    {
        var host = Process(10, HostPath, 10);
        var reader = BrowserTree(host);
        reader.Processes.Remove(40);
        var result = WindowsBrowserPeerIdentity.FindBrowser(host, reader);
        Assert.Equal(30, result.BrowserProcessId);
    }

    [Fact]
    public async Task NativeLocalPipeIsAsyncFirstInstanceAndKernelReportsActualPeerPids()
    {
        var pipeName = "DX5SourceSwitcher.Test." + Guid.NewGuid().ToString("N");
        using var server = WindowsBrowserPeerIdentity.CreateServer(pipeName);
        Assert.True(server.IsAsync);
        Assert.Throws<Win32Exception>(() => WindowsBrowserPeerIdentity.CreateServer(pipeName));
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var accepting = server.WaitForConnectionAsync(cancellation.Token);
        await client.ConnectAsync(cancellation.Token);
        await accepting;
        Assert.Equal(Environment.ProcessId, WindowsBrowserPeerIdentity.GetClientProcessId(server.SafePipeHandle));
        Assert.Equal(Environment.ProcessId, WindowsBrowserPeerIdentity.GetServerProcessId(client.SafePipeHandle));
    }

    [Fact]
    public void KernelPeerLookupFailsClosedForInvalidHandles()
    {
        using var invalid = new SafePipeHandle(new IntPtr(-1), false);
        Assert.Throws<Win32Exception>(() => WindowsBrowserPeerIdentity.GetClientProcessId(invalid));
        Assert.Throws<Win32Exception>(() => WindowsBrowserPeerIdentity.GetServerProcessId(invalid));
        Assert.Throws<ArgumentNullException>(() => WindowsBrowserPeerIdentity.GetClientProcessId(null!));
        Assert.Throws<ArgumentNullException>(() => WindowsBrowserPeerIdentity.GetServerProcessId(null!));
    }

    [Fact]
    public void CurrentPipeNameUsesStableLocalAccountSessionIdentifier()
    {
        var first = WindowsBrowserPeerIdentity.CurrentPipeName;
        Assert.Equal(first, WindowsBrowserPeerIdentity.CurrentPipeName);
        Assert.Matches("^DX5SourceSwitcher\\.Browser\\.[0-9A-F]{24}$", first);
        WindowsBrowserPeerIdentity.ValidatePipeName(first);
    }

    private static WindowsBrowserProcess Process(int id, string path, int seconds = 0) =>
        new(id, Started.AddSeconds(seconds), path, Sid, 1);

    private static FakeReader BrowserTree(WindowsBrowserProcess host)
    {
        var reader = new FakeReader();
        reader.Processes[host.ProcessId] = host;
        reader.Processes[20] = Process(20, BrowserPath, 5);
        reader.Processes[30] = Process(30, BrowserPath);
        reader.Processes[40] = Process(40, @"C:\Windows\explorer.exe", -5);
        reader.Parents[host.ProcessId] = 20;
        reader.Parents[20] = 30;
        reader.Parents[30] = 40;
        reader.Registered.Add(BrowserPath.ToUpperInvariant());
        return reader;
    }

    private static FakeReader HeliumShellTree(WindowsBrowserProcess host)
    {
        var reader = new FakeReader();
        reader.Processes[host.ProcessId] = host;
        reader.Processes[20] = Process(20, Path.Combine(Environment.SystemDirectory, "cmd.exe"), 5);
        reader.Processes[30] = Process(30, @"C:\Program Files\imput\Helium\Application\chrome.exe");
        reader.Parents[host.ProcessId] = 20;
        reader.Parents[20] = 30;
        reader.Registered.Add(reader.Processes[30].ExecutablePath.ToUpperInvariant());
        return reader;
    }

    private sealed class FakeReader : IWindowsBrowserProcessReader
    {
        public Dictionary<int, WindowsBrowserProcess> Processes { get; } = [];
        public Dictionary<int, int> Parents { get; } = [];
        public HashSet<string> Registered { get; } = new(StringComparer.Ordinal);
        public List<int> ReadIds { get; } = [];
        public Func<int, int, WindowsBrowserProcess?>? AfterRead { get; set; }

        public WindowsBrowserProcess Read(int processId)
        {
            ReadIds.Add(processId);
            if (!Processes.TryGetValue(processId, out var process)) throw new Win32Exception(87);
            AfterRead?.Invoke(processId, ReadIds.Count(id => id == processId));
            return process;
        }
        public int ParentOf(int processId) => Parents.GetValueOrDefault(processId);
        public bool IsRegisteredGecko(string executablePath) =>
            Registered.Contains(WindowsBrowserPeerIdentity.NormalizeExecutablePath(executablePath));
    }
}

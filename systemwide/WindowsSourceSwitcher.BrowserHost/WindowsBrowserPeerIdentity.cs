using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace WindowsSourceSwitcher.BrowserBridge;

public sealed record BrowserPeerIdentity(string BrowserExecutablePath, int BrowserProcessId,
    DateTimeOffset BrowserStartedAtUtc, int HostProcessId);

/// <summary>
/// Local compatibility/identity guards for the browser native-messaging transport. They do not
/// establish a malware boundary against arbitrary code running as the same Windows user.
/// Neither claimed process identities nor commands are accepted from browser JSON.
/// </summary>
public static class WindowsBrowserPeerIdentity
{
    internal const string HostExecutableName = "WindowsSourceSwitcher.BrowserHost.exe";
    internal const string TrayExecutableName = "WindowsSourceSwitcher.Tray.exe";
    internal const string PortableTrayExecutableName = "SourceSwitcher.exe";
    private const int MaxBrowserAncestors = 16;

    public static string CurrentPipeName
    {
        get
        {
            var current = new NativeProcessReader().Read(Environment.ProcessId);
            var identity = Encoding.UTF8.GetBytes(current.UserSid + "|" + current.SessionId);
            return "DX5SourceSwitcher.Browser." + Convert.ToHexString(SHA256.HashData(identity))[..24];
        }
    }

    /// <summary>
    /// Creates one local, asynchronous, first-instance pipe with an explicit user-only DACL and
    /// medium mandatory label. This intentionally permits a medium browser host to contact an
    /// elevated controller, unlike PipeOptions.CurrentUserOnly's matching-elevation policy.
    /// The caller owns connection timeouts, cancellation and the returned stream's lifetime.
    /// </summary>
    public static NamedPipeServerStream CreateServer(string pipeName)
    {
        ValidatePipeName(pipeName);
        var current = new NativeProcessReader().Read(Environment.ProcessId);
        var descriptor = BuildPipeSecurityDescriptor(current.UserSid);
        if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptor(descriptor, 1, out var securityDescriptor, out _))
            throw LastError("Cannot create the browser bridge security descriptor");
        try
        {
            var attributes = new Native.SecurityAttributes
            {
                Length = Marshal.SizeOf<Native.SecurityAttributes>(),
                SecurityDescriptor = securityDescriptor,
                InheritHandle = false
            };
            var pipe = Native.CreateNamedPipe(@"\\.\pipe\" + pipeName,
                Native.PipeAccessDuplex | Native.FileFlagOverlapped | Native.FileFlagFirstPipeInstance,
                Native.PipeRejectRemoteClients, 1, 65_536, 65_536, 0, ref attributes);
            if (pipe.IsInvalid)
            {
                var error = LastError("Cannot create the browser bridge pipe");
                pipe.Dispose();
                throw error;
            }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, pipe); }
            catch { pipe.Dispose(); throw; }
        }
        finally { Native.LocalFree(securityDescriptor); }
    }

    public static BrowserPeerIdentity ValidateClient(SafePipeHandle pipe, string expectedHostPath)
    {
        var reader = new NativeProcessReader();
        var current = reader.Read(Environment.ProcessId);
        RequireTrayExecutable(current.ExecutablePath);
        RequireInstalledPeerPath(current.ExecutablePath, expectedHostPath, HostExecutableName);
        var host = reader.Read(GetClientProcessId(pipe));
        ValidateExpectedPeer(host, current, expectedHostPath);
        var result = FindBrowser(host, reader);
        RequireUnchanged(host, reader.Read(host.ProcessId));
        return result;
    }

    public static void ValidateServer(SafePipeHandle pipe, string expectedTrayPath)
    {
        var reader = new NativeProcessReader();
        var current = reader.Read(Environment.ProcessId);
        RequireExecutable(current.ExecutablePath, HostExecutableName);
        RequireTrayExecutable(expectedTrayPath);
        RequireInstalledPeerPath(current.ExecutablePath, expectedTrayPath, Path.GetFileName(expectedTrayPath));
        var server = reader.Read(GetServerProcessId(pipe));
        ValidateExpectedPeer(server, current, expectedTrayPath);
        RequireUnchanged(server, reader.Read(server.ProcessId));
    }

    public static BrowserPeerIdentity ValidateHostBrowserParent()
    {
        var reader = new NativeProcessReader();
        var host = reader.Read(Environment.ProcessId);
        RequireExecutable(host.ExecutablePath, HostExecutableName);
        var result = FindBrowser(host, reader);
        RequireUnchanged(host, reader.Read(host.ProcessId));
        return result;
    }

    public static bool IsRegisteredBrowserExecutable(string executablePath)
    {
        try { return new NativeProcessReader().IsRegisteredGecko(executablePath); }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            return false;
        }
    }

    public static int GetClientProcessId(SafePipeHandle pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!Native.GetNamedPipeClientProcessId(pipe, out var processId))
            throw LastError("Cannot identify the browser bridge client");
        return CheckedProcessId(processId);
    }

    public static int GetServerProcessId(SafePipeHandle pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!Native.GetNamedPipeServerProcessId(pipe, out var processId))
            throw LastError("Cannot identify the browser bridge server");
        return CheckedProcessId(processId);
    }

    internal static string BuildPipeSecurityDescriptor(string userSid)
    {
        // Parsing prevents a SID supplied by an internal/test caller from becoming SDDL syntax.
        var sid = new SecurityIdentifier(userSid).Value;
        return $"D:P(A;;GA;;;{sid})S:(ML;;NW;;;ME)";
    }

    internal static void ValidatePipeName(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (pipeName.Length > 128 || pipeName.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            throw new ArgumentException("The browser pipe name must be a bounded local identifier.", nameof(pipeName));
    }

    internal static string NormalizeExecutablePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!string.Equals(path, path.Trim(), StringComparison.Ordinal)
            || path.Length > 32_767 || path.Any(char.IsControl))
            throw new ArgumentException("An absolute Windows executable path is required.", nameof(path));
        var normalized = path.Replace('/', '\\');
        if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal))
            normalized = normalized[4..];
        // Production images must be local files, never UNC/device/alternate-stream identities.
        if (normalized.Length < 4 || !char.IsAsciiLetter(normalized[0]) || normalized[1] != ':'
            || normalized[2] != '\\' || normalized.AsSpan(2).Contains(':')
            || !normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || normalized.IndexOfAny(['"', '<', '>', '|', '?', '*']) >= 0)
            throw new ArgumentException("A local absolute Windows executable path is required.", nameof(path));
        return Path.GetFullPath(normalized).ToUpperInvariant();
    }

    internal static void RequireInstalledPeerPath(string ownPath, string expectedPath, string expectedName)
    {
        var own = NormalizeExecutablePath(ownPath);
        var expected = NormalizeExecutablePath(expectedPath);
        RequireExecutable(expected, expectedName);
        string installedPeer;
        if (expectedName == HostExecutableName)
        {
            RequireTrayExecutable(own);
            installedPeer = Path.Combine(Path.GetDirectoryName(own)!, "browser-helper", HostExecutableName);
        }
        else if (expectedName is TrayExecutableName or PortableTrayExecutableName)
            installedPeer = ExpectedTrayPathForHost(own);
        else throw new UnauthorizedAccessException("An unknown executable cannot be a browser bridge peer.");
        if (!string.Equals(NormalizeExecutablePath(installedPeer), expected, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The browser bridge peer is outside this published application folder.");
    }

    internal static string ExpectedTrayPathForHost(string hostPath)
    {
        var host = NormalizeExecutablePath(hostPath);
        RequireExecutable(host, HostExecutableName);
        var directory = Path.GetDirectoryName(host)!;
        if (!string.Equals(Path.GetFileName(directory), "browser-helper", StringComparison.OrdinalIgnoreCase)
            || Path.GetDirectoryName(directory) is not { } root)
            throw new UnauthorizedAccessException("The browser helper must run from this app's browser-helper folder.");
        var portable = Path.Combine(root, PortableTrayExecutableName);
        return File.Exists(portable) ? portable : Path.Combine(root, TrayExecutableName);
    }

    internal static void ValidateExpectedPeer(WindowsBrowserProcess peer, WindowsBrowserProcess current, string expectedPath)
    {
        RequireSameUserAndSession(peer, current);
        if (!string.Equals(NormalizeExecutablePath(peer.ExecutablePath), NormalizeExecutablePath(expectedPath), StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The browser bridge peer executable is not the expected application.");
    }

    internal static BrowserPeerIdentity FindBrowser(WindowsBrowserProcess host, IWindowsBrowserProcessReader reader)
    {
        var visited = new HashSet<int> { host.ProcessId };
        var parentId = reader.ParentOf(host.ProcessId);
        if (parentId <= 0 || !visited.Add(parentId))
            throw new UnauthorizedAccessException("The native host has no verifiable browser parent.");
        var browser = reader.Read(parentId);
        RequireParent(browser, host);
        WindowsBrowserProcess? launchShell = null;
        if (IsSystemNativeMessagingShell(browser.ExecutablePath))
        {
            // Chromium may launch a native-messaging executable through cmd.exe. Accept
            // exactly one OS shell under registered Helium, never arbitrary ancestors.
            launchShell = browser;
            parentId = reader.ParentOf(launchShell.ProcessId);
            if (parentId <= 0 || !visited.Add(parentId))
                throw new UnauthorizedAccessException("The native messaging shell has no verifiable browser parent.");
            browser = reader.Read(parentId);
            RequireParent(browser, launchShell);
            if (!string.Equals(Path.GetFileName(NormalizeExecutablePath(browser.ExecutablePath)), "chrome.exe", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Only a recognized Helium browser may use the native messaging shell launcher.");
        }
        if (!reader.IsRegisteredGecko(browser.ExecutablePath))
            throw new UnauthorizedAccessException("The native host was not started by a locally registered Firefox, Zen or Helium browser.");
        var browserPath = NormalizeExecutablePath(browser.ExecutablePath);
        var root = browser;
        for (var depth = 0; depth < MaxBrowserAncestors; depth++)
        {
            parentId = reader.ParentOf(root.ProcessId);
            if (parentId <= 0) break;
            if (!visited.Add(parentId))
                throw new UnauthorizedAccessException("The native host's process ancestry is cyclic.");
            WindowsBrowserProcess parent;
            try { parent = reader.Read(parentId); }
            catch (Win32Exception) { break; } // The browser launcher's parent can legitimately have exited.
            if (!string.Equals(NormalizeExecutablePath(parent.ExecutablePath), browserPath, StringComparison.Ordinal))
                break;
            RequireParent(parent, root);
            root = parent;
            if (depth == MaxBrowserAncestors - 1)
                throw new UnauthorizedAccessException("The browser process ancestry exceeds the verification limit.");
        }
        RequireUnchanged(browser, reader.Read(browser.ProcessId));
        RequireUnchanged(root, reader.Read(root.ProcessId));
        if (launchShell is not null) RequireUnchanged(launchShell, reader.Read(launchShell.ProcessId));
        return new(browserPath, root.ProcessId, root.StartedAtUtc, host.ProcessId);
    }

    internal static bool IsSystemNativeMessagingShell(string executablePath)
    {
        var path = NormalizeExecutablePath(executablePath);
        var systemShell = NormalizeExecutablePath(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        if (string.Equals(path, systemShell, StringComparison.Ordinal)) return true;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return !string.IsNullOrWhiteSpace(windows) && string.Equals(path,
            NormalizeExecutablePath(Path.Combine(windows, "SysWOW64", "cmd.exe")), StringComparison.Ordinal);
    }

    private static void RequireTrayExecutable(string path)
    {
        if (!string.Equals(Path.GetFileName(path), PortableTrayExecutableName, StringComparison.OrdinalIgnoreCase))
            RequireExecutable(path, TrayExecutableName);
    }

    private static void RequireExecutable(string path, string expectedName)
    {
        if (!string.Equals(Path.GetFileName(NormalizeExecutablePath(path)), expectedName, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The browser bridge must run as its published Windows executable.");
    }

    private static void RequireSameUserAndSession(WindowsBrowserProcess process, WindowsBrowserProcess context)
    {
        if (process.ProcessId <= 0 || process.SessionId < 0
            || !string.Equals(process.UserSid, context.UserSid, StringComparison.Ordinal)
            || process.SessionId != context.SessionId)
            throw new UnauthorizedAccessException("The browser bridge peer has a different Windows user or session.");
    }

    private static void RequireParent(WindowsBrowserProcess parent, WindowsBrowserProcess child)
    {
        RequireSameUserAndSession(parent, child);
        if (parent.ProcessId == child.ProcessId || parent.StartedAtUtc > child.StartedAtUtc)
            throw new UnauthorizedAccessException("A parent process identity was reused or is inconsistent.");
    }

    private static void RequireUnchanged(WindowsBrowserProcess before, WindowsBrowserProcess after)
    {
        if (before != after)
            throw new UnauthorizedAccessException("A process identity changed while validating the browser bridge.");
    }

    private static int CheckedProcessId(uint processId) => processId is > 0 and <= int.MaxValue
        ? (int)processId : throw new UnauthorizedAccessException("The browser bridge peer process ID is invalid.");

    private static Win32Exception LastError(string message) => new(Marshal.GetLastWin32Error(), message);

    private sealed class NativeProcessReader : IWindowsBrowserProcessReader
    {
        private Dictionary<int, int>? _parents;

        public WindowsBrowserProcess Read(int processId)
        {
            if (processId <= 0) throw new UnauthorizedAccessException("A process ID is invalid.");
            using var process = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, processId);
            if (process.IsInvalid) throw LastError("Cannot query a browser bridge process");
            if (!Native.GetExitCodeProcess(process, out var exitCode)) throw LastError("Cannot query process liveness");
            if (exitCode != Native.StillActive) throw new UnauthorizedAccessException("A browser bridge process has exited.");
            if (!Native.GetProcessTimes(process, out var creation, out _, out _, out _))
                throw LastError("Cannot read the browser bridge process lifetime");
            var path = new StringBuilder(32_768);
            var length = path.Capacity;
            if (!Native.QueryFullProcessImageName(process, 0, path, ref length))
                throw LastError("Cannot read the browser bridge process image");
            if (!Native.OpenProcessToken(process, Native.TokenQuery, out var token))
                throw LastError("Cannot query a browser bridge process user");
            using (token)
            {
                Native.GetTokenInformation(token, 1, IntPtr.Zero, 0, out var required);
                if (required is 0 or > 65_536) throw LastError("Cannot size the process user identity");
                var buffer = Marshal.AllocHGlobal(checked((int)required));
                try
                {
                    if (!Native.GetTokenInformation(token, 1, buffer, required, out _))
                        throw LastError("Cannot read the process user identity");
                    var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
                    if (!Native.GetTokenSessionId(token, 12, out var session, sizeof(uint), out _))
                        throw LastError("Cannot read the process logon session");
                    if (session > int.MaxValue) throw new UnauthorizedAccessException("The process session is invalid.");
                    var fileTime = ((long)(uint)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime;
                    return new(processId, new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime)),
                        NormalizeExecutablePath(path.ToString()), sid, (int)session);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }

        public int ParentOf(int processId)
        {
            _parents ??= ReadParents();
            return _parents.TryGetValue(processId, out var parent) ? parent : 0;
        }

        public bool IsRegisteredGecko(string executablePath)
        {
            var path = NormalizeExecutablePath(executablePath);
            var name = Path.GetFileName(path);
            if (name is not ("FIREFOX.EXE" or "ZEN.EXE" or "CHROME.EXE")) return false;
            if (!IsRegisteredPath(path, name)) return false;
            try
            {
                var version = FileVersionInfo.GetVersionInfo(path);
                return IsSupportedBrowserProduct(name, version.ProductName, version.OriginalFilename, version.CompanyName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
            {
                return false;
            }
        }

        private static Dictionary<int, int> ReadParents()
        {
            using var snapshot = Native.CreateToolhelp32Snapshot(2, 0);
            if (snapshot.IsInvalid) throw LastError("Cannot inspect browser process ancestry");
            var entry = new Native.ProcessEntry { Size = (uint)Marshal.SizeOf<Native.ProcessEntry>() };
            if (!Native.Process32First(snapshot, ref entry)) throw LastError("Cannot read browser process ancestry");
            var parents = new Dictionary<int, int>();
            do
            {
                if (entry.ProcessId is > 0 and <= int.MaxValue && entry.ParentProcessId <= int.MaxValue)
                    parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                if (parents.Count >= 32_768)
                    throw new UnauthorizedAccessException("The process census exceeds the browser bridge limit.");
            } while (Native.Process32Next(snapshot, ref entry));
            if (Marshal.GetLastWin32Error() != 18) throw LastError("The browser process census was incomplete");
            return parents;
        }

        private static bool IsRegisteredPath(string path, string name)
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var app = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + name);
                    if (RegistryPathEquals(app?.GetValue(null) as string, path)) return true;
                    if (name == "CHROME.EXE")
                    {
                        using var helium = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\imput Helium");
                        if (RegisteredHeliumInstallMatches(helium?.GetValue("DisplayName") as string,
                            helium?.GetValue("Publisher") as string, helium?.GetValue("InstallLocation") as string, path))
                            return true;
                    }
                    using var browsers = root.OpenSubKey(@"Software\Clients\StartMenuInternet");
                    if (browsers is null) continue;
                    foreach (var browserKey in browsers.GetSubKeyNames().Take(128))
                    {
                        using var command = browsers.OpenSubKey(browserKey + @"\shell\open\command");
                        if (RegistryCommandPathEquals(command?.GetValue(null) as string, path)) return true;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
            return false;
        }
    }

    internal static bool IsGeckoProduct(string executableName, string? productName, string? originalFilename, string? companyName)
    {
        if (!string.Equals(originalFilename, executableName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(companyName, "Mozilla Corporation", StringComparison.OrdinalIgnoreCase)) return false;
        return executableName.Equals("zen.exe", StringComparison.OrdinalIgnoreCase)
            ? productName is "Zen" or "Zen Browser"
            : executableName.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase)
                && productName is "Firefox" or "Mozilla Firefox" or "Firefox Developer Edition" or "Nightly";
    }

    internal static bool IsSupportedBrowserProduct(string executableName, string? productName,
        string? originalFilename, string? companyName) =>
        IsGeckoProduct(executableName, productName, originalFilename, companyName)
        || (string.Equals(executableName, "chrome.exe", StringComparison.OrdinalIgnoreCase)
            && string.Equals(originalFilename, "chrome.exe", StringComparison.OrdinalIgnoreCase)
            && string.Equals(productName, "Helium", StringComparison.Ordinal)
            && string.Equals(companyName, "The Helium Authors", StringComparison.Ordinal));

    internal static bool RegisteredHeliumInstallMatches(string? displayName, string? publisher,
        string? installLocation, string expectedPath)
    {
        if (!string.Equals(displayName, "Helium", StringComparison.Ordinal)
            || !string.Equals(publisher, "The Helium Authors", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(installLocation)) return false;
        try { return RegistryPathEquals(Path.Combine(installLocation.Trim(), "chrome.exe"), expectedPath); }
        catch (ArgumentException) { return false; }
    }

    internal static bool RegistryPathEquals(string? registeredPath, string expectedPath)
    {
        if (string.IsNullOrWhiteSpace(registeredPath)) return false;
        var candidate = Environment.ExpandEnvironmentVariables(registeredPath.Trim());
        if (candidate.Length >= 2 && candidate[0] == '"' && candidate[^1] == '"') candidate = candidate[1..^1];
        try { return string.Equals(NormalizeExecutablePath(candidate), NormalizeExecutablePath(expectedPath), StringComparison.Ordinal); }
        catch (ArgumentException) { return false; }
    }

    internal static bool RegistryCommandPathEquals(string? command, string expectedPath)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var candidate = command.TrimStart();
        if (candidate.StartsWith('"'))
        {
            var end = candidate.IndexOf('"', 1);
            return end > 1 && RegistryPathEquals(candidate[1..end], expectedPath);
        }
        // Unquoted paths are accepted only without spaces; never split/execute a shell command.
        var split = candidate.IndexOfAny([' ', '\t']);
        return RegistryPathEquals(split < 0 ? candidate : candidate[..split], expectedPath);
    }

    private static class Native
    {
        internal const uint PipeAccessDuplex = 3, FileFlagOverlapped = 0x40000000,
            FileFlagFirstPipeInstance = 0x00080000, PipeRejectRemoteClients = 8;
        internal const uint ProcessQueryLimitedInformation = 0x1000, TokenQuery = 8, StillActive = 259;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SecurityAttributes
        {
            internal int Length;
            internal IntPtr SecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct ProcessEntry
        {
            internal uint Size, Usage, ProcessId;
            internal UIntPtr DefaultHeapId;
            internal uint ModuleId, Threads, ParentProcessId;
            internal int BasePriority;
            internal uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string ExecutableFile;
        }

        [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string descriptor, uint revision, out IntPtr securityDescriptor, out uint size);
        [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
            uint outputBufferSize, uint inputBufferSize, uint defaultTimeout, ref SecurityAttributes attributes);
        [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(SafeProcessHandle process, out System.Runtime.InteropServices.ComTypes.FILETIME creation,
            out System.Runtime.InteropServices.ComTypes.FILETIME exit, out System.Runtime.InteropServices.ComTypes.FILETIME kernel,
            out System.Runtime.InteropServices.ComTypes.FILETIME user);
        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr information, uint length, out uint required);
        [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenSessionId(SafeAccessTokenHandle token, int informationClass, out uint information, uint length, out uint required);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SnapshotHandle CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32First(SnapshotHandle snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32Next(SnapshotHandle snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);

        internal sealed class SnapshotHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
        {
            protected override bool ReleaseHandle() => CloseHandle(handle);
        }
    }
}

internal sealed record WindowsBrowserProcess(int ProcessId, DateTimeOffset StartedAtUtc, string ExecutablePath, string UserSid, int SessionId);

internal interface IWindowsBrowserProcessReader
{
    WindowsBrowserProcess Read(int processId);
    int ParentOf(int processId);
    bool IsRegisteredGecko(string executablePath);
}

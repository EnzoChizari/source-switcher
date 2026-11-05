using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace WindowsSourceSwitcher.Tray;

/// <summary>Request-scoped local CLI shutdown; Windows applies the creator's default event ACL.</summary>
internal sealed class InstanceShutdownSignal : IDisposable
{
    private readonly EventWaitHandle _signal;
    private readonly RegisteredWaitHandle _wait;
    private int _disposed;

    internal InstanceShutdownSignal(Action shutdown, string? name = null)
    {
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, name ?? CurrentName);
        _wait = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) =>
        {
            if (Volatile.Read(ref _disposed) == 0) shutdown();
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    internal static string CurrentName
    {
        get
        {
            var path = Path.GetFullPath(Environment.ProcessPath!).ToUpperInvariant();
            using var user = WindowsIdentity.GetCurrent();
            var scope = path + "|" + user.User!.Value;
            return @"Local\SourceSwitcher.Shutdown." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        }
    }

    internal static bool Request(string? name = null)
    {
        try { using var signal = EventWaitHandle.OpenExisting(name ?? CurrentName); return signal.Set(); }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _wait.Unregister(null);
        _signal.Dispose();
    }
}

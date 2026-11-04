using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.Tray.Audio;

internal sealed record ProcessIdentitySnapshot(StreamProcessIdentity Identity, string ExecutablePath);

/// <summary>
/// Keeps a handle to the observed process instance. Checking that handle's liveness is
/// cheaper than reopening a process and reading its modules on every audio census, and
/// cannot mistake a new process with a recycled PID for the previous owner.
/// </summary>
internal sealed class ProcessIdentityCache : IDisposable
{
    internal const int MaximumEntries = 256;
    private readonly object _sync = new();
    private readonly Dictionary<int, Entry> _entries = new();
    private readonly Func<int, IProcessIdentityLease?> _open;
    private long _access;
    private bool _disposed;

    public ProcessIdentityCache(Func<int, IProcessIdentityLease?>? open = null) =>
        _open = open ?? NativeProcessIdentityLease.Open;

    internal int Count { get { lock (_sync) return _entries.Count; } }

    public ProcessIdentitySnapshot? Read(int processId, string? expectedPath = null)
    {
        if (processId <= 0) return null;
        lock (_sync)
        {
            if (_disposed) return null;
            var access = ++_access;
            if (_entries.TryGetValue(processId, out var entry))
            {
                if (entry.Lease.IsRunning)
                {
                    entry.LastAccess = access;
                    return Matches(entry.Lease.Snapshot, expectedPath);
                }
                Remove(processId);
            }

            // Failed queries are intentionally not cached: access or process state can
            // change before the next initialization or session census.
            var lease = _open(processId);
            if (lease is null) return null;
            if (!lease.IsRunning)
            {
                lease.Dispose();
                return null;
            }

            if (_entries.Count >= MaximumEntries)
            {
                foreach (var cached in _entries.ToArray())
                    if (!cached.Value.Lease.IsRunning) Remove(cached.Key);
                if (_entries.Count >= MaximumEntries)
                    Remove(_entries.MinBy(pair => pair.Value.LastAccess).Key);
            }

            _entries.Add(processId, new Entry(lease, access));
            return Matches(lease.Snapshot, expectedPath);
        }
    }

    private static ProcessIdentitySnapshot? Matches(ProcessIdentitySnapshot snapshot, string? expectedPath) =>
        expectedPath is null || StringComparer.OrdinalIgnoreCase.Equals(snapshot.ExecutablePath, expectedPath)
            ? snapshot : null;

    private void Remove(int processId)
    {
        if (_entries.Remove(processId, out var entry)) entry.Lease.Dispose();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _entries.Values) entry.Lease.Dispose();
            _entries.Clear();
        }
    }

    private sealed class Entry(IProcessIdentityLease lease, long access)
    {
        public IProcessIdentityLease Lease { get; } = lease;
        public long LastAccess { get; set; } = access;
    }
}

internal interface IProcessIdentityLease : IDisposable
{
    ProcessIdentitySnapshot Snapshot { get; }
    bool IsRunning { get; }
}

internal sealed class NativeProcessIdentityLease : IProcessIdentityLease
{
    private readonly SafeProcessHandle _handle;
    private NativeProcessIdentityLease(SafeProcessHandle handle, ProcessIdentitySnapshot snapshot)
    { _handle = handle; Snapshot = snapshot; }

    public ProcessIdentitySnapshot Snapshot { get; }
    public bool IsRunning => !_handle.IsClosed && !_handle.IsInvalid
        && WaitForSingleObject(_handle, 0) == 0x102; // WAIT_TIMEOUT means the process is still running.

    public static IProcessIdentityLease? Open(int processId)
    {
        // SYNCHRONIZE permits a zero-time liveness check. The query permission suffices
        // for the executable path and creation time; module enumeration is unnecessary.
        var handle = OpenProcess(0x0010_0000 | 0x1000, false, processId);
        if (handle.IsInvalid) { handle.Dispose(); return null; }
        try
        {
            var path = new StringBuilder(512);
            var length = path.Capacity;
            if (!QueryFullProcessImageName(handle, 0, path, ref length))
            {
                if (Marshal.GetLastWin32Error() != 122) { handle.Dispose(); return null; }
                path.EnsureCapacity(32_768);
                length = path.Capacity;
                if (!QueryFullProcessImageName(handle, 0, path, ref length))
                { handle.Dispose(); return null; }
            }
            if (!GetProcessTimes(handle, out var created, out _, out _, out _))
            { handle.Dispose(); return null; }
            var startedAt = new DateTimeOffset(DateTime.FromFileTimeUtc(created));
            return new NativeProcessIdentityLease(handle, new(new(processId, startedAt), path.ToString()));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}

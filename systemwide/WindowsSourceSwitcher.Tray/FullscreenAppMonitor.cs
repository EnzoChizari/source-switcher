using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WindowsSourceSwitcher.Tray.Audio;

namespace WindowsSourceSwitcher.Tray;

/// <summary>Out-of-context window notifications; no injection or audio polling.</summary>
internal sealed class FullscreenAppMonitor : IDisposable
{
    private readonly System.Windows.Forms.Timer _probe = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 160 };
    private readonly WinEventProc _callback;
    private nint _foregroundHook, _locationHook;
    private (nint Window, uint Pid) _current;
    private bool _enabled;
    public event Action? Changed;
    public string? Error { get; private set; }
    public nint CurrentFullscreenWindow => _enabled ? ReadForeground().Window : 0;

    public FullscreenAppMonitor()
    {
        _callback = OnWindowEvent; // Keep the native callback rooted until unhooked.
        _probe.Tick += (_, _) => Probe();
        _debounce.Tick += (_, _) => { _debounce.Stop(); Probe(); };
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (enabled)
        {
            _foregroundHook = SetWinEventHook(3, 3, 0, _callback, 0, 0, 0);
            _locationHook = SetWinEventHook(0x800B, 0x800B, 0, _callback, 0, 0, 0);
            Error = _foregroundHook == 0 || _locationHook == 0
                ? "Window notifications unavailable; checking fullscreen once a second." : null;
            _probe.Start();
            Probe();
        }
        else
        {
            _probe.Stop();
            _debounce.Stop();
            if (_foregroundHook != 0) UnhookWinEvent(_foregroundHook);
            if (_locationHook != 0) UnhookWinEvent(_locationHook);
            _foregroundHook = _locationHook = 0;
            _current = default;
        }
    }

    private void OnWindowEvent(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (!_enabled || (eventType == 0x800B && (objectId != 0 || window != GetForegroundWindow()))) return;
        // Start, rather than restart, so continuous location events cannot starve observation.
        if (!_debounce.Enabled) _debounce.Start();
    }

    private void Probe()
    {
        var current = ReadForeground();
        if (current == _current) return;
        _current = current;
        Changed?.Invoke();
    }

    public FullscreenCandidate? Resolve(IReadOnlyList<PlatformAudioSource> sources, string? retainedSourceKey = null)
    {
        if (!_enabled) return null;
        var (window, pid) = ReadForeground();
        if (window == 0) return null;
        try
        {
            using var process = Process.GetProcessById(checked((int)pid));
            var path = process.MainModule?.FileName;
            if (path is null || process.HasExited) return null;
            var source = sources.FirstOrDefault(source => source.CanPersist
                && (source.IsActive || StringComparer.OrdinalIgnoreCase.Equals(source.Identity, retainedSourceKey))
                && (StringComparer.OrdinalIgnoreCase.Equals(path, source.ExecutablePath)
                    || source.ProcessExecutablePaths.Values.Any(value => StringComparer.OrdinalIgnoreCase.Equals(path, value))));
            // Recheck after process access: focus or process ownership may have changed.
            if (source is null || ReadForeground() != (window, pid)) return null;
            return new(window, source.Identity, source.ProcessName);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException or OverflowException)
        { return null; }
    }

    private static (nint Window, uint Pid) ReadForeground()
    {
        var window = GetForegroundWindow();
        if (window == 0 || !IsWindowVisible(window) || IsIconic(window)) return default;
        GetWindowThreadProcessId(window, out var pid);
        if (pid == 0 || pid == Environment.ProcessId) return default;
        var name = new StringBuilder(128);
        GetClassName(window, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return default;
        if (DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return default;
        if (!GetClientRect(window, out var client)) return default;
        var top = new Point(client.Left, client.Top);
        var bottom = new Point(client.Right, client.Bottom);
        if (!ClientToScreen(window, ref top) || !ClientToScreen(window, ref bottom)) return default;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref info)) return default;
        return CoversMonitor(Rectangle.FromLTRB(top.X, top.Y, bottom.X, bottom.Y),
            Rectangle.FromLTRB(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom))
            ? (window, pid) : default;
    }

    internal static bool CoversMonitor(Rectangle client, Rectangle monitor) => monitor.Width > 0 && monitor.Height > 0
        && client.Width > 0 && client.Height > 0
        && Math.Abs((long)client.Left - monitor.Left) <= 2 && Math.Abs((long)client.Top - monitor.Top) <= 2
        && Math.Abs((long)client.Right - monitor.Right) <= 2 && Math.Abs((long)client.Bottom - monitor.Bottom) <= 2;

    public void Dispose()
    {
        SetEnabled(false);
        _debounce.Dispose();
        _probe.Dispose();
        GC.KeepAlive(_callback);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    private delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int max);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(nint hook);
}

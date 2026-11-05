using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowsSourceSwitcher.Tray.Audio;

/// <summary>Native preflight keeps UI Automation unloaded for absent or minimized players.</summary>
internal static class PlayerWindowLocator
{
    internal static (nint Handle, int Pid)? Find(string processName, Func<string, bool> matchesPath)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            (nint Handle, int Pid)? selected = null;
            foreach (var process in processes)
            {
                var handle = process.MainWindowHandle;
                if (handle == 0 || process.MainModule?.FileName is not { } path || !matchesPath(path)) continue;
                if (selected is not null) return null;
                selected = (handle, process.Id);
            }
            return selected is { } window && IsWindowVisible(window.Handle) && !IsIconic(window.Handle) ? window : null;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return null; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
}

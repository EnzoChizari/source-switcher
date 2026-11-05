using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Session;

namespace WindowsSourceSwitcher.Tray.Audio;

/// <summary>Reclaims this product's abandoned kernel traces after a crash or forced exit.</summary>
internal static class OwnedAudioTraceSessions
{
    internal static int RecoverAbandoned()
    {
        try
        {
            return RecoverAbandoned(TraceEventSession.GetActiveSessionNames(), IsAlive, name =>
            {
                using var session = new TraceEventSession(name, TraceEventSessionOptions.Attach);
                session.Stop(noThrow: false);
            });
        }
        catch (Exception) { return 0; } // Best effort; the normal observer still reports its own startup errors.
    }

    internal static int RecoverAbandoned(IEnumerable<string> names, Func<int, bool?> isAlive, Action<string> stop)
    {
        var recovered = 0;
        foreach (var name in names)
        {
            if (!TryGetOwner(name, out var pid)) continue;
            try
            {
                // Unknown ownership/access and every live PID are deliberately left alone,
                // including a PID reused by another process. Never restart a live trace.
                if (isAlive(pid) is not false || isAlive(pid) is not false) continue;
                stop(name);
                recovered++;
            }
            catch (Exception) { /* One inaccessible trace must not prevent observation or other cleanup. */ }
        }
        return recovered;
    }

    internal static bool TryGetOwner(string name, out int pid)
    {
        pid = 0;
        const string live = "DX5SourceSwitcher.StreamRates.", snapshot = "DX5SourceSwitcher.StreamSnapshot.";
        string suffix;
        if (name.StartsWith(live, StringComparison.Ordinal))
        {
            var parts = name[live.Length..].Split('.');
            if (parts.Length == 1) suffix = parts[0]; // Older releases used PID-only names.
            else if (parts.Length == 2 && Guid.TryParseExact(parts[1], "N", out _)) suffix = parts[0];
            else return false;
        }
        else if (name.StartsWith(snapshot, StringComparison.Ordinal))
        {
            var parts = name[snapshot.Length..].Split('.');
            if (parts.Length != 2 || !Guid.TryParseExact(parts[1], "N", out _)) return false;
            suffix = parts[0];
        }
        else return false;
        return suffix.Length > 0 && suffix.All(char.IsAsciiDigit) && suffix[0] != '0'
            && int.TryParse(suffix, out pid) && pid > 0;
    }

    private static bool? IsAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (Exception) { return null; }
    }
}

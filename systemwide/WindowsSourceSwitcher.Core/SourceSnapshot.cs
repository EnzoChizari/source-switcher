namespace WindowsSourceSwitcher.Core;

/// <summary>One executable, with all of its current audio-session processes grouped together.</summary>
public sealed record SourceSnapshot(
    string Key,
    string ProcessName,
    string ExecutablePath,
    IReadOnlyList<int> ProcessIds,
    bool IsActive,
    string SessionToken);

public static class SourceIdentity
{
    /// <summary>
    /// Windows executable paths, rather than PIDs or session IDs, survive a process restart.
    /// Callers must skip inaccessible executable paths instead of inventing a PID identity.
    /// </summary>
    public static string FromExecutablePath(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        // Use Windows path semantics even when the pure library is tested on another OS.
        var path = executablePath.Trim().Replace('/', '\\');
        if (!(path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
            && !path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("A full Windows executable path is required.", nameof(executablePath));
        }

        var parts = path.Split('\\');
        if (parts.Any(part => part is "." or ".."))
            throw new ArgumentException("Executable paths must be absolute and normalized.", nameof(executablePath));

        return path.ToUpperInvariant();
    }
}

public static class SampleRates
{
    public const int MinimumHz = 8_000;
    public const int MaximumHz = 768_000;

    // A plausibility check only. Device support is checked by the Windows endpoint adapter.
    public static bool IsValid(int sampleRateHz) => sampleRateHz is >= MinimumHz and <= MaximumHz;
}

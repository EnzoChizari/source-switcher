using System.Diagnostics.CodeAnalysis;

namespace WindowsSourceSwitcher.Core;

/// <summary>
/// Identifies the two known executable roles directly in an Apple Music WindowsApps package.
/// This is a path identity rule, not a signature or installation-authenticity check.
/// Package version, architecture, drive and package directory remain part of the identity.
/// </summary>
public static class AppleMusicIdentity
{
    private const string Prefix = "APPLEINC.APPLEMUSICWIN_";
    private const string Publisher = "__NZYJ5CX40TTQA";

    public static bool IsOfficialExecutable(string? path) => TryCanonicalPath(path) is not null;

    /// <summary>Normalizes recognized package roles to AppleMusic.exe and preserves other paths unchanged.</summary>
    [return: NotNullIfNotNull(nameof(path))]
    public static string? CanonicalPath(string? path) => TryCanonicalPath(path) ?? path;

    private static string? TryCanonicalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32767 || path != path.Trim()
            || path.Any(char.IsControl)) return null;
        var normalized = path.Replace('/', '\\');
        if (normalized.Length < 4 || !char.IsAsciiLetter(normalized[0]) || normalized[1] != ':'
            || normalized[2] != '\\' || normalized.AsSpan(2).Contains(':')
            || normalized.IndexOfAny(['"', '<', '>', '|', '?', '*']) >= 0) return null;
        normalized = normalized.ToUpperInvariant();
        var parts = normalized.Split('\\');
        // Default system installation, or a Store package installed on another drive.
        var windowsAppsIndex = parts.Length == 4 && parts[1] == "WINDOWSAPPS" ? 1
            : parts.Length == 5 && parts[1] is "PROGRAM FILES" or "PROGRAM FILES (X86)"
                && parts[2] == "WINDOWSAPPS" ? 2 : -1;
        if (windowsAppsIndex < 0 || parts[^1] is not ("APPLEMUSIC.EXE" or "AMPLIBRARYAGENT.EXE")) return null;
        var package = parts[windowsAppsIndex + 1];
        if (!package.StartsWith(Prefix, StringComparison.Ordinal) || !package.EndsWith(Publisher, StringComparison.Ordinal)) return null;
        var versionAndArchitecture = package[Prefix.Length..^Publisher.Length].Split('_');
        if (versionAndArchitecture.Length != 2
            || versionAndArchitecture[1] is not ("X64" or "X86" or "ARM64" or "ARM" or "NEUTRAL")) return null;
        var version = versionAndArchitecture[0].Split('.');
        if (version.Length != 4 || version.Any(part => part.Length is < 1 or > 5
            || part.Any(character => character is < '0' or > '9') || !ushort.TryParse(part, out _))) return null;
        return string.Join("\\", parts[..^1]) + "\\APPLEMUSIC.EXE";
    }

    /// <summary>Only recognized roles in the exact same package directory are equivalent.</summary>
    public static bool Equivalent(string? left, string? right) => TryCanonicalPath(left) is { } canonical
        && string.Equals(canonical, TryCanonicalPath(right), StringComparison.Ordinal);

    /// <summary>Collapses legacy helper preferences without losing a confirmed target or an explicit main-executable value.</summary>
    public static bool MigrateSettings(SourceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var selected = CanonicalPath(settings.SelectedSourceKey);
        var selectedChanged = !string.Equals(selected, settings.SelectedSourceKey, StringComparison.Ordinal);
        var revision = settings.SelectionRevision;
        if (!StringComparer.OrdinalIgnoreCase.Equals(selected, settings.SelectedSourceKey)) revision = checked(revision + 1);
        var displayName = IsHelper(settings.SelectedSourceKey) && IsDefaultHelperName(settings.SelectedSourceDisplayName)
            ? "AppleMusic" : settings.SelectedSourceDisplayName;
        var held = CanonicalPath(settings.LastKnownSourceKey);
        var favorites = MigrateDictionary(settings.FavoriteSources, static name => IsDefaultHelperName(name) ? "AppleMusic" : name);
        var pins = MigrateDictionary(settings.PinnedSources, static name => IsDefaultHelperName(name) ? "AppleMusic" : name);
        var manual = MigrateDictionary(settings.ManualSampleRatesHz, static rate => rate);
        var changed = selectedChanged || !string.Equals(held, settings.LastKnownSourceKey, StringComparison.Ordinal)
            || !string.Equals(displayName, settings.SelectedSourceDisplayName, StringComparison.Ordinal)
            || !SameEntries(settings.FavoriteSources, favorites) || !SameEntries(settings.PinnedSources, pins)
            || !SameEntries(settings.ManualSampleRatesHz, manual);
        if (!changed) return false;
        settings.SelectedSourceKey = selected;
        settings.SelectedSourceDisplayName = displayName;
        settings.SelectionRevision = revision;
        settings.LastKnownSourceKey = held;
        settings.FavoriteSources = favorites;
        settings.PinnedSources = pins;
        settings.ManualSampleRatesHz = manual;
        settings.Validate();
        return true;
    }

    private static Dictionary<string, T> MigrateDictionary<T>(Dictionary<string, T> original, Func<T, T> helperValue)
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        // Explicit main-executable settings win when both old roles were configured.
        foreach (var pair in original.Where(pair => !IsHelper(pair.Key))) result[CanonicalPath(pair.Key)!] = pair.Value;
        foreach (var pair in original.Where(pair => IsHelper(pair.Key)))
            result.TryAdd(CanonicalPath(pair.Key)!, helperValue(pair.Value));
        return result;
    }

    private static bool SameEntries<T>(Dictionary<string, T> left, Dictionary<string, T> right)
    {
        if (left.Count != right.Count) return false;
        var exact = new Dictionary<string, T>(left, StringComparer.Ordinal);
        return right.All(pair => exact.TryGetValue(pair.Key, out var value) && EqualityComparer<T>.Default.Equals(value, pair.Value));
    }

    private static bool IsHelper(string? path) => IsOfficialExecutable(path)
        && path!.Replace('/', '\\').EndsWith("\\AMPLibraryAgent.exe", StringComparison.OrdinalIgnoreCase);
    private static bool IsDefaultHelperName(string? name) => string.Equals(name, "AMPLibraryAgent", StringComparison.OrdinalIgnoreCase);
}

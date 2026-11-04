namespace WindowsSourceSwitcher.Core;

/// <summary>Favorites identify executable paths, so an application stays pinned across process restarts.</summary>
public static class SourceFavorites
{
    public const int MaxFavorites = 128;
    public const int MaxDisplayNameLength = 256;
    public const int MaxSourceKeyLength = 32767;

    /// <summary>Transient process identities, system sounds and excluded applications cannot be pinned.</summary>
    public static bool CanFavorite(string? sourceKey, string? displayName) =>
        IsStableExecutablePath(sourceKey)
        && !string.IsNullOrWhiteSpace(displayName)
        && displayName.Length <= MaxDisplayNameLength
        && !displayName.Any(char.IsControl)
        && !SourceExclusions.IsExcluded(sourceKey, displayName);

    internal static void Validate(Dictionary<string, string>? favorites)
    {
        if (favorites is null)
            throw new InvalidDataException("Favorite sources are missing.");
        if (favorites.Count > MaxFavorites)
            throw new InvalidDataException($"At most {MaxFavorites} favorite sources may be saved.");

        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, displayName) in favorites)
        {
            // Previously saved excluded applications must remain loadable so the host can remove them.
            if (!IsStableExecutablePath(key)
                || string.IsNullOrWhiteSpace(displayName)
                || displayName.Length > MaxDisplayNameLength
                || displayName.Any(char.IsControl)
                || !identities.Add(SourceIdentity.FromExecutablePath(key)))
            {
                throw new InvalidDataException("Favorites require unique absolute Windows executable paths and nonempty display names of at most 256 characters.");
            }
        }
    }

    private static bool IsStableExecutablePath(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxSourceKeyLength
            || !string.Equals(key, key.Trim(), StringComparison.Ordinal)
            || !key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return false;

        // Validate Windows syntax explicitly rather than using the operating system running Core tests.
        var path = key.Replace('/', '\\');
        string[] parts;
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
        {
            parts = path[3..].Split('\\');
        }
        else if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            parts = path[2..].Split('\\');
            // A UNC identity needs a server, share and executable, not a device namespace.
            if (parts.Length < 3 || parts[0] is "." or "?")
                return false;
        }
        else
        {
            return false;
        }

        return parts.All(part => part.Length > 0 && part is not "." and not ".."
            && part[^1] is not ' ' and not '.'
            && !part.Any(character => char.IsControl(character) || character is ':' or '<' or '>' or '"' or '|' or '?' or '*'));
    }
}

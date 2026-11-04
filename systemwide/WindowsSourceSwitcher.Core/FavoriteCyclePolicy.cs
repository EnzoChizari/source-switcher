namespace WindowsSourceSwitcher.Core;

/// <summary>Hotkey cycling uses current active sessions; saved favorites alone never invent a source.</summary>
public static class FavoriteCyclePolicy
{
    public static string? Next(IEnumerable<SourceSnapshot> live, SourceSettings settings, int direction)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(settings);
        if (direction is not -1 and not 1)
            throw new ArgumentOutOfRangeException(nameof(direction), "Cycle direction must be -1 or 1.");

        var eligible = live.Where(source => source.IsActive
                && SourceFavorites.CanFavorite(source.Key, source.ProcessName)
                && SourceFavorites.CanFavorite(source.ExecutablePath, source.ProcessName)
                && StringComparer.OrdinalIgnoreCase.Equals(SourceIdentity.FromExecutablePath(source.Key),
                    SourceIdentity.FromExecutablePath(source.ExecutablePath))
                && settings.IsFavorite(source.Key)
                && !SourceExclusions.IsExcluded(source.ExecutablePath, source.ProcessName))
            .Select(source => SourceIdentity.FromExecutablePath(source.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (eligible.Length == 0) return null;

        var selected = Array.FindIndex(eligible,
            key => StringComparer.OrdinalIgnoreCase.Equals(key, settings.SelectedSourceKey));
        if (selected < 0) return direction > 0 ? eligible[0] : eligible[^1];
        return eligible[(selected + direction + eligible.Length) % eligible.Length];
    }
}

namespace WindowsSourceSwitcher.Core;

/// <summary>Applications intentionally omitted from master-source selection and rate detection.</summary>
public static class SourceExclusions
{
    /// <summary>Remove excluded saved choices without choosing a replacement or changing the endpoint.</summary>
    public static bool ApplyToSettings(SourceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var changed = false;
        foreach (var key in settings.ManualSampleRatesHz.Keys.Where(key => IsExcluded(key)).ToArray())
            changed |= settings.ManualSampleRatesHz.Remove(key);
        foreach (var key in settings.FavoriteSources.Keys.Where(key => IsExcluded(key)).ToArray())
            changed |= settings.FavoriteSources.Remove(key);
        foreach (var key in settings.PinnedSources.Keys.Where(key => IsExcluded(key)).ToArray())
            changed |= settings.PinnedSources.Remove(key);
        if (IsExcluded(settings.SelectedSourceKey, settings.SelectedSourceDisplayName))
        {
            settings.SelectedSourceKey = settings.SelectedSourceDisplayName = null;
            settings.LastKnownSourceKey = null;
            settings.LastKnownSampleRateHz = null;
            settings.SelectionRevision = checked(settings.SelectionRevision + 1);
            changed = true;
        }
        return changed;
    }

    public static bool IsExcluded(string? executablePath, string? processName = null)
    {
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            // Handle both separators explicitly so this pure policy also works outside Windows.
            var separator = Math.Max(executablePath.LastIndexOf('\\'), executablePath.LastIndexOf('/'));
            return executablePath.AsSpan(separator + 1).Equals("ShareX.exe", StringComparison.OrdinalIgnoreCase);
        }

        // ProcessName omits the extension. Only use it when the path is inaccessible.
        return string.Equals(processName, "ShareX", StringComparison.OrdinalIgnoreCase);
    }
}

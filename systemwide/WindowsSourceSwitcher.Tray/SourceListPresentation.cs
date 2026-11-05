using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;

namespace WindowsSourceSwitcher.Tray;

/// <summary>A saved row has no live source: a favorite must not invent a session or rate evidence.</summary>
internal sealed record SourceListEntry(string Identity, string Name, PlatformAudioSource? LiveSource,
    bool IsFavorite, bool IsMaster, FavoritePlaybackState MediaPlayback = FavoritePlaybackState.Unknown, bool IsPinned = false)
{
    public string DisplayName => IsFavorite ? "★ " + Name : IsPinned ? "◆ " + Name : Name;
    public string SessionText => LiveSource is null ? "No session"
        : !LiveSource.IsActive ? "Idle / paused"
        : MediaPlayback == FavoritePlaybackState.NotPlaying ? "Paused"
        : MediaPlayback == FavoritePlaybackState.Playing ? "Playing" : "Active";
    public bool CanSelectSaved => LiveSource is null && SourceFavorites.CanFavorite(Identity, Name);
}

internal static class SourceListPresentation
{
    public static IReadOnlyList<SourceListEntry> Build(IReadOnlyList<PlatformAudioSource> sources,
        SourceSettings settings, IReadOnlyDictionary<string, FavoritePlaybackState>? playback = null)
    {
        var entries = new Dictionary<string, SourceListEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources.Where(source => !SourceExclusions.IsExcluded(source.ExecutablePath, source.ProcessName))
            .OrderByDescending(source => source.IsActive))
        {
            entries.TryAdd(source.Identity, new(source.Identity, source.ProcessName, source,
                settings.IsFavorite(source.Identity), KeysEqual(source.Identity, settings.SelectedSourceKey),
                playback?.GetValueOrDefault(source.Identity) ?? FavoritePlaybackState.Unknown, settings.IsPinned(source.Identity)));
        }

        foreach (var (identity, name) in settings.FavoriteSources)
        {
            if (SourceFavorites.CanFavorite(identity, name))
                entries.TryAdd(identity, new(identity, name, null, true, KeysEqual(identity, settings.SelectedSourceKey), IsPinned: settings.IsPinned(identity)));
        }

        foreach (var (identity, name) in settings.PinnedSources)
            if (SourceFavorites.CanFavorite(identity, name))
                entries.TryAdd(identity, new(identity, name, null, false, KeysEqual(identity, settings.SelectedSourceKey), IsPinned: true));

        if (settings.SelectedSourceKey is { } selected && !entries.ContainsKey(selected))
        {
            var name = settings.SelectedSourceDisplayName ?? "Selected app";
            entries.Add(selected, new(selected, name, null, settings.IsFavorite(selected), true, IsPinned: settings.IsPinned(selected)));
        }

        return entries.Values.OrderByDescending(entry => entry.IsMaster)
            .ThenByDescending(entry => entry.IsFavorite)
            .ThenByDescending(entry => entry.IsPinned)
            .ThenByDescending(entry => entry.LiveSource?.IsActive == true)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Identity, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static SourceRateDisplay DescribeSavedRate(SourceListEntry entry, SourceSettings settings)
    {
        var manual = settings.ManualSampleRatesHz.FirstOrDefault(item => KeysEqual(item.Key, entry.Identity));
        if (manual.Key is not null && SampleRates.IsValid(manual.Value))
            return new(SourceRatePresentation.FormatRate(manual.Value, settings.FollowSourceBitDepth
                    ? settings.ManualBitDepths.GetValueOrDefault(entry.Identity) : null) + " manual",
                "Saved manual target for this app; no current audio session or native/content rate was observed.");

        if (entry.IsMaster)
        {
            if (KeysEqual(settings.LastKnownSourceKey, entry.Identity)
                && settings.LastKnownSampleRateHz is { } held && SampleRates.IsValid(held))
                return new(SourceRatePresentation.FormatRate(held) + " held",
                    "Selected app has no current audio session. Holding its last confirmed output rate.");
            return new("Rate held", "Selected app has no current audio session. Current output rate is unchanged.");
        }

        return new("Not observed", "Saved source has no audio session on this output. Selecting it waits for the app; it does not launch the app or supply stream evidence.");
    }

    private static bool KeysEqual(string? left, string? right) => StringComparer.OrdinalIgnoreCase.Equals(left, right);
}

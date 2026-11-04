namespace WindowsSourceSwitcher.Core;

public sealed class SourceSettings
{
    public int SchemaVersion { get; set; } = 1;
    public bool SwitchingEnabled { get; set; } = true;
    public bool StreamRateDetectionEnabled { get; set; } = true;
    public bool FollowSourceBitDepth { get; set; }
    public bool GlobalHotkeysEnabled { get; set; }
    public bool FavoriteAutoSwitchEnabled { get; set; }
    public bool FullscreenAutoSwitchEnabled { get; set; }
    public bool BrowserRateDetectionEnabled { get; set; }
    public bool CatalogRateDetectionEnabled { get; set; }
    public bool TutorialPending { get; set; }
    public string? TargetEndpointId { get; set; }
    public string? SelectedSourceKey { get; set; }
    public string? SelectedSourceDisplayName { get; set; }
    public long SelectionRevision { get; set; }
    public Dictionary<string, int> ManualSampleRatesHz { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> FavoriteSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> PinnedSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? LastKnownSourceKey { get; set; }
    public int? LastKnownSampleRateHz { get; set; }

    public bool IsFavorite(string sourceKey)
    {
        if (!SourceFavorites.CanFavorite(sourceKey, "Application") || FavoriteSources is null)
            return false;
        var identity = SourceIdentity.FromExecutablePath(sourceKey);
        return FavoriteSources.Keys.Any(key => StringComparer.OrdinalIgnoreCase.Equals(
            SourceIdentity.FromExecutablePath(key), identity));
    }

    /// <summary>Pinning changes presentation only; it never selects a master or changes its rate policy.</summary>
    public bool SetFavorite(string sourceKey, string displayName, bool favorite)
        => SetSavedSource(FavoriteSources, sourceKey, displayName, favorite);

    public bool IsPinned(string sourceKey) => SourceFavorites.CanFavorite(sourceKey, "Application")
        && PinnedSources is not null && PinnedSources.Keys.Any(key => StringComparer.OrdinalIgnoreCase.Equals(
            SourceIdentity.FromExecutablePath(key), SourceIdentity.FromExecutablePath(sourceKey)));

    public bool SetPinned(string sourceKey, string displayName, bool pinned)
        => SetSavedSource(PinnedSources, sourceKey, displayName, pinned);

    private static bool SetSavedSource(Dictionary<string, string> saved, string sourceKey, string displayName, bool favorite)
    {
        if (!SourceFavorites.CanFavorite(sourceKey, displayName))
            throw new ArgumentException("Only an eligible absolute executable path and a valid display name can be pinned.");
        SourceFavorites.Validate(saved);
        var identity = SourceIdentity.FromExecutablePath(sourceKey);
        var existing = saved.Keys.FirstOrDefault(key => StringComparer.OrdinalIgnoreCase.Equals(
            SourceIdentity.FromExecutablePath(key), identity));
        if (!favorite)
            return existing is not null && saved.Remove(existing);
        if (existing is not null)
        {
            if (string.Equals(saved[existing], displayName, StringComparison.Ordinal))
                return false;
            saved[existing] = displayName;
            return true;
        }
        if (saved.Count >= SourceFavorites.MaxFavorites)
            throw new InvalidOperationException($"At most {SourceFavorites.MaxFavorites} applications can be pinned.");
        saved.Add(identity, displayName);
        return true;
    }

    public void SelectMaster(string sourceKey, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (!StringComparer.OrdinalIgnoreCase.Equals(sourceKey, SelectedSourceKey))
        {
            LastKnownSourceKey = null;
            LastKnownSampleRateHz = null;
        }

        SelectedSourceKey = sourceKey;
        SelectedSourceDisplayName = displayName;
        SelectionRevision = checked(SelectionRevision + 1);
    }

    /// <summary>Call only after a successful endpoint change or a confirmed current-rate match.</summary>
    public void RememberAppliedRate(string sourceKey, int sampleRateHz)
    {
        if (SelectedSourceKey is null || !StringComparer.OrdinalIgnoreCase.Equals(sourceKey, SelectedSourceKey))
            throw new InvalidOperationException("Cannot remember a rate for an unselected source.");
        if (!SampleRates.IsValid(sampleRateHz))
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));

        LastKnownSourceKey = SelectedSourceKey;
        LastKnownSampleRateHz = sampleRateHz;
    }

    public void Validate()
    {
        if (SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported settings schema version: {SchemaVersion}.");
        if (SelectionRevision < 0)
            throw new InvalidDataException("Selection revision must be nonnegative.");
        if (ManualSampleRatesHz is null)
            throw new InvalidDataException("Manual sample-rate settings are missing.");
        SourceFavorites.Validate(FavoriteSources);
        SourceFavorites.Validate(PinnedSources);
        if (TargetEndpointId is not null && string.IsNullOrWhiteSpace(TargetEndpointId))
            throw new InvalidDataException("Target endpoint ID cannot be empty.");
        if (SelectedSourceKey is not null && string.IsNullOrWhiteSpace(SelectedSourceKey))
            throw new InvalidDataException("Selected source key cannot be empty.");
        if (SelectedSourceKey is null && SelectedSourceDisplayName is not null)
            throw new InvalidDataException("A display name requires a selected source.");
        if (SelectedSourceKey is not null && string.IsNullOrWhiteSpace(SelectedSourceDisplayName))
            throw new InvalidDataException("A selected source requires a display name.");
        if ((LastKnownSourceKey is null) != (LastKnownSampleRateHz is null))
            throw new InvalidDataException("A held rate and its source must be stored together.");
        if (LastKnownSourceKey is not null &&
            !StringComparer.OrdinalIgnoreCase.Equals(LastKnownSourceKey, SelectedSourceKey))
            throw new InvalidDataException("The held rate must belong to the selected source.");
        if (LastKnownSampleRateHz is { } held && !SampleRates.IsValid(held))
            throw new InvalidDataException("Held sample rate is outside the allowed range.");

        var uniqueKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, rate) in ManualSampleRatesHz)
        {
            if (string.IsNullOrWhiteSpace(key) || !SampleRates.IsValid(rate) || !uniqueKeys.Add(key))
                throw new InvalidDataException("Manual sample rates require a source key and a rate between 8000 and 768000 Hz.");
        }
    }
}

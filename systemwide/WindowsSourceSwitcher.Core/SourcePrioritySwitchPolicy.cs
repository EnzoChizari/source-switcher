namespace WindowsSourceSwitcher.Core;

/// <summary>The host owns fullscreen priority. This policy chooses favorites, pins, then
/// regular applications, with continuous target-specific confirmation and no peak/silence guesses.</summary>
public sealed class SourcePrioritySwitchPolicy
{
    public static readonly TimeSpan Confirmation = TimeSpan.FromMilliseconds(500);
    private string? _candidate, _selected;
    private int _candidateTier;
    private long _revision = -1;
    private TimeSpan? _since, _last, _recoveryUntil;
    public bool HasPendingHandoff => _candidate is not null && _since is not null;

    public void CancelPendingHandoff() => Reset();

    public FavoriteAutoSwitchDecision Update(SourceSettings settings, IReadOnlyList<SourceSnapshot> sources,
        IReadOnlyDictionary<string, FavoritePlaybackState> playback, TimeSpan now, bool allowFromNoMaster = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(now.Ticks);
        if (_last is { } last && now < last) { Reset(); _recoveryUntil = null; }
        _last = now;
        if (_revision != settings.SelectionRevision || !Same(_selected, settings.SelectedSourceKey))
        { Reset(); _selected = settings.SelectedSourceKey; _revision = settings.SelectionRevision; }
        if (!settings.SwitchingEnabled || !settings.FavoriteAutoSwitchEnabled
            || (settings.SelectedSourceKey is null && !allowFromNoMaster))
        { Reset(); _recoveryUntil = null; return Hold("Automatic source priority is off or the master was released."); }
        if (_recoveryUntil is { } until && now < until)
            return Hold("Waiting for playback recovery after the format change.", until - now);
        _recoveryUntil = null;

        string? Identity(SourceSnapshot source) => SourceFavorites.CanFavorite(source.Key, source.ProcessName)
            && SourceFavorites.CanFavorite(source.ExecutablePath, source.ProcessName)
            && Same(SourceIdentity.FromExecutablePath(source.Key), SourceIdentity.FromExecutablePath(source.ExecutablePath))
                ? SourceIdentity.FromExecutablePath(source.Key) : null;
        var active = sources.Where(s => s.IsActive && Identity(s) is not null)
            .Select(s => Identity(s)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        FavoritePlaybackState Playback(string key)
        {
            var matches = playback.Where(pair => SourceFavorites.CanFavorite(pair.Key, "Application")
                && Same(SourceIdentity.FromExecutablePath(pair.Key), key)).Take(2).ToArray();
            return matches.Length == 1 && Enum.IsDefined(matches[0].Value) ? matches[0].Value : FavoritePlaybackState.Unknown;
        }
        var current = settings.SelectedSourceKey;
        var currentState = current is not null && active.Contains(current) ? Playback(current) : FavoritePlaybackState.NotPlaying;
        if (currentState == FavoritePlaybackState.Unknown)
        { Reset(); return Hold("Current playback is uncertain; keeping the master."); }
        var eligible = active.Where(key => Playback(key) == FavoritePlaybackState.Playing)
            .OrderBy(key => Tier(settings, key)).ThenBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();
        var next = eligible.FirstOrDefault();
        if (next is null) { Reset(); return Hold("No eligible application is confirmed playing; holding the rate."); }
        // Keep a playing peer within the winning tier instead of oscillating by name.
        if (currentState == FavoritePlaybackState.Playing && current is not null
            && Tier(settings, current) <= Tier(settings, next))
        { Reset(); return Hold("Current master is playing in the highest eligible priority tier."); }
        if (!Same(_candidate, next) || _candidateTier != Tier(settings, next))
        { _candidate = next; _candidateTier = Tier(settings, next); _since = now; }
        // All ordinary handoffs, including Apple Music, use the same continuous
        // confirmation. Playback rebuild observation is a separate host concern.
        var remaining = Confirmation - (now - _since!.Value);
        return remaining > TimeSpan.Zero ? Hold("Confirming the next playing source by priority.", remaining)
            : new(next, null, "Selected the highest playing tier: favorites → pinned → regular apps.");
    }

    public void SuspendForRecovery(TimeSpan now, TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(now.Ticks);
        ArgumentOutOfRangeException.ThrowIfNegative(duration.Ticks);
        Reset(); _last = now;
        _recoveryUntil = now.Ticks > TimeSpan.MaxValue.Ticks - duration.Ticks ? TimeSpan.MaxValue : now + duration;
    }
    public static int Tier(SourceSettings settings, string key) => settings.IsFavorite(key) ? 0 : settings.IsPinned(key) ? 1 : 2;
    private void Reset() { _candidate = null; _since = null; }
    private static bool Same(string? a, string? b) => StringComparer.OrdinalIgnoreCase.Equals(a, b);
    private static FavoriteAutoSwitchDecision Hold(string detail, TimeSpan? delay = null) => new(null, delay, detail);
}

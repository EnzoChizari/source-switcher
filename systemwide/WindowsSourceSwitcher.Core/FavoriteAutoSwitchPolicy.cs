namespace WindowsSourceSwitcher.Core;

/// <summary>Resolved playback evidence, independent of audio peak level or selected sample rate.</summary>
public enum FavoritePlaybackState
{
    Unknown,
    NotPlaying,
    Playing
}

public sealed record FavoriteAutoSwitchDecision(string? TargetSourceKey, TimeSpan? RetryAfter, string Detail);

/// <summary>
/// Advances a stopped favorite master to the next playing favorite. It never creates a master
/// after release, takes over a manually selected nonfavorite, or infers playback from silence.
/// </summary>
public sealed class FavoriteAutoSwitchPolicy
{
    // These confirmations overlap: an already-playing candidate can take over after 500 ms total.
    public static readonly TimeSpan StopDebounce = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan PlayingDebounce = TimeSpan.FromMilliseconds(500);

    private bool _bound;
    private bool _enabled;
    private string? _selectedKey;
    private long _selectionRevision;
    private TimeSpan? _lastTime;
    private TimeSpan? _stoppedSince;
    private string? _candidateKey;
    private TimeSpan? _candidateSince;
    private TimeSpan? _recoveryUntil;

    public FavoriteAutoSwitchDecision Update(SourceSettings settings, IReadOnlyList<SourceSnapshot> live,
        IReadOnlyDictionary<string, FavoritePlaybackState> playback, TimeSpan monotonicNow)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentOutOfRangeException.ThrowIfNegative(monotonicNow.Ticks, nameof(monotonicNow));

        var enabled = settings.SwitchingEnabled && settings.FavoriteAutoSwitchEnabled;
        var selectedKey = StableIdentity(settings.SelectedSourceKey);
        if (!_bound || _enabled != enabled || _selectionRevision != settings.SelectionRevision
            || !StringComparer.OrdinalIgnoreCase.Equals(_selectedKey, selectedKey))
        {
            ClearPending();
            // A host may suspend recovery before the first poll. Later manual selections end it.
            if (_bound) _recoveryUntil = null;
            _bound = true;
            _enabled = enabled;
            _selectedKey = selectedKey;
            _selectionRevision = settings.SelectionRevision;
        }

        if (_lastTime is { } lastTime && monotonicNow < lastTime)
        {
            ClearPending();
            _recoveryUntil = null;
            _lastTime = monotonicNow;
            return Hold("Playback timing restarted after a clock discontinuity.", StopDebounce);
        }
        _lastTime = monotonicNow;

        if (!enabled || selectedKey is null || !settings.IsFavorite(selectedKey))
        {
            ClearPending();
            _recoveryUntil = null;
            return Hold(!enabled ? "Automatic favorite switching is disabled."
                : selectedKey is null ? "No favorite master is selected."
                : "The manually selected master is not a favorite.");
        }

        if (_recoveryUntil is { } recoveryUntil)
        {
            if (monotonicNow < recoveryUntil)
                return Hold("Waiting for playback to recover after changing the device format.", recoveryUntil - monotonicNow);
            _recoveryUntil = null;
            ClearPending();
        }

        var active = live.Where(source => source.IsActive && EligibleIdentity(source) is not null)
            .Select(source => EligibleIdentity(source)!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // An absent or inactive audio session cannot remain Playing through stale playback evidence.
        var currentState = active.Contains(selectedKey) ? PlaybackFor(playback, selectedKey) : FavoritePlaybackState.NotPlaying;
        if (currentState != FavoritePlaybackState.NotPlaying)
        {
            ClearPending();
            return Hold(currentState == FavoritePlaybackState.Playing
                ? "The current favorite master is playing."
                : "Current playback is uncertain; keeping the selected master.");
        }

        _stoppedSince ??= monotonicNow;
        var eligible = active.Where(key => settings.IsFavorite(key)
                && !StringComparer.OrdinalIgnoreCase.Equals(key, selectedKey)
                && PlaybackFor(playback, key) == FavoritePlaybackState.Playing)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        // Keep the selected executable's position as the anchor even when its process has closed.
        var next = eligible.FirstOrDefault(key => StringComparer.OrdinalIgnoreCase.Compare(key, selectedKey) > 0)
            ?? eligible.FirstOrDefault();
        if (next is null)
        {
            _candidateKey = null;
            _candidateSince = null;
            return Hold("No other favorite is confirmed playing.");
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(_candidateKey, next))
        {
            _candidateKey = next;
            _candidateSince = monotonicNow;
        }
        var stoppedRemaining = StopDebounce - (monotonicNow - _stoppedSince.Value);
        var playingRemaining = PlayingDebounce - (monotonicNow - _candidateSince!.Value);
        var retryAfter = stoppedRemaining > playingRemaining ? stoppedRemaining : playingRemaining;
        if (retryAfter > TimeSpan.Zero)
            return Hold("Confirming the stopped master and the next playing favorite.", retryAfter);

        return new(next, null, "The selected favorite stopped; the next favorite is confirmed playing.");
    }

    /// <summary>Ignore transient session invalidation caused by the host's own successful format write.</summary>
    public void SuspendForRecovery(TimeSpan monotonicNow, TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(monotonicNow.Ticks, nameof(monotonicNow));
        ArgumentOutOfRangeException.ThrowIfNegative(duration.Ticks, nameof(duration));
        ClearPending();
        _lastTime = monotonicNow;
        _recoveryUntil = monotonicNow.Ticks > TimeSpan.MaxValue.Ticks - duration.Ticks
            ? TimeSpan.MaxValue : monotonicNow + duration;
    }

    private void ClearPending()
    {
        _stoppedSince = null;
        _candidateKey = null;
        _candidateSince = null;
    }

    private static FavoriteAutoSwitchDecision Hold(string detail, TimeSpan? retryAfter = null) => new(null, retryAfter, detail);

    private static string? StableIdentity(string? key) => SourceFavorites.CanFavorite(key, "Application")
        ? SourceIdentity.FromExecutablePath(key!) : null;

    private static string? EligibleIdentity(SourceSnapshot source)
    {
        if (!SourceFavorites.CanFavorite(source.Key, source.ProcessName)
            || !SourceFavorites.CanFavorite(source.ExecutablePath, source.ProcessName)) return null;
        var identity = SourceIdentity.FromExecutablePath(source.Key);
        return StringComparer.OrdinalIgnoreCase.Equals(identity, SourceIdentity.FromExecutablePath(source.ExecutablePath))
            ? identity : null;
    }

    private static FavoritePlaybackState PlaybackFor(IReadOnlyDictionary<string, FavoritePlaybackState> playback, string key)
    {
        // Normalize even when the caller's dictionary uses an ordinal comparer or forward slashes.
        var matches = playback.Where(entry => StringComparer.OrdinalIgnoreCase.Equals(StableIdentity(entry.Key), key)).ToArray();
        return matches.Length == 1 && Enum.IsDefined(matches[0].Value) ? matches[0].Value : FavoritePlaybackState.Unknown;
    }
}

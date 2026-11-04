using System.Collections.ObjectModel;
using Windows.Media.Control;
using WindowsLosslessSwitcher.Services;
using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.Tray.Audio;

/// <summary>Playback metadata from GSMTC, separate from Windows audio-session activity.</summary>
public sealed record FavoritePlaybackSession(string AppUserModelId, FavoritePlaybackState State);

/// <summary>
/// An immutable playback snapshot. GSMTC does not expose the audio process ID, so associations
/// require a unique executable identity; uncertain or conflicting associations remain unknown.
/// </summary>
public sealed class FavoritePlaybackSnapshot
{
    internal const string AppleMusicAppId = "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App";
    private readonly ReadOnlyCollection<FavoritePlaybackSession> _sessions;
    private readonly bool _complete;

    public FavoritePlaybackSnapshot(long version, IEnumerable<FavoritePlaybackSession> sessions) : this(version, sessions, true) { }

    internal FavoritePlaybackSnapshot(long version, IEnumerable<FavoritePlaybackSession> sessions, bool complete)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        Version = version;
        _sessions = Array.AsReadOnly(sessions.Take(FavoritePlaybackMonitor.MaximumSessions + 1).ToArray());
        _complete = complete && _sessions.Count <= FavoritePlaybackMonitor.MaximumSessions;
    }

    public long Version { get; }

    public IReadOnlyDictionary<string, FavoritePlaybackState> Resolve(IReadOnlyList<PlatformAudioSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var apps = sources.GroupBy(source => source.Identity, StringComparer.OrdinalIgnoreCase).ToArray();
        var result = apps.ToDictionary(group => group.Key, _ => FavoritePlaybackState.Unknown,
            StringComparer.OrdinalIgnoreCase);
        // An incomplete census cannot establish that an executable or media session is unique.
        if (!_complete)
            return new ReadOnlyDictionary<string, FavoritePlaybackState>(result);

        var evidence = new Dictionary<string, List<FavoritePlaybackState>>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in _sessions)
        {
            var id = session.AppUserModelId;
            if (string.IsNullOrEmpty(id)) continue;
            var apple = IsAppleSessionId(id);
            if (!apple && !IsBareExecutableId(id)) continue;

            var matches = apps.Where(group => group.Any(source => source.CanPersist
                && (apple ? source.SupportsAutomaticRate
                    : StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(source.ExecutablePath), id))))
                .Take(2).ToArray();
            if (matches.Length != 1) continue;
            if (!evidence.TryGetValue(matches[0].Key, out var states))
                evidence[matches[0].Key] = states = [];
            states.Add(Enum.IsDefined(session.State) ? session.State : FavoritePlaybackState.Unknown);
        }

        foreach (var (key, states) in evidence)
            result[key] = states.All(state => state == states[0]) ? states[0] : FavoritePlaybackState.Unknown;
        return new ReadOnlyDictionary<string, FavoritePlaybackState>(result);
    }

    /// <summary>
    /// Applications without a media-session integration may use Windows session activity.
    /// A potentially associated but uncertain media session never falls back to that weaker signal.
    /// </summary>
    public IReadOnlyDictionary<string, FavoritePlaybackState> ResolveForFavorites(IReadOnlyList<PlatformAudioSource> sources)
    {
        var resolved = Resolve(sources);
        var result = new Dictionary<string, FavoritePlaybackState>(resolved, StringComparer.OrdinalIgnoreCase);
        foreach (var app in sources.GroupBy(source => source.Identity, StringComparer.OrdinalIgnoreCase))
        {
            if (!app.Any(source => source.IsActive))
            {
                result[app.Key] = FavoritePlaybackState.NotPlaying;
                continue;
            }
            if (resolved[app.Key] != FavoritePlaybackState.Unknown
                || !_complete
                || app.Any(RequiresMediaPlaybackStatus)
                || _sessions.Any(session => app.Any(source => CouldMatch(session.AppUserModelId, source))))
                continue;
            result[app.Key] = FavoritePlaybackState.Playing;
        }
        return new ReadOnlyDictionary<string, FavoritePlaybackState>(result);
    }

    private static bool RequiresMediaPlaybackStatus(PlatformAudioSource source) => source.SupportsAutomaticRate
        || StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(source.ExecutablePath), "Spotify.exe");

    private static bool CouldMatch(string? id, PlatformAudioSource source) => source.CanPersist
        && !string.IsNullOrEmpty(id)
        && (IsAppleSessionId(id) ? source.SupportsAutomaticRate
            : IsBareExecutableId(id) && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(source.ExecutablePath), id));

    private static bool IsAppleSessionId(string id) => StringComparer.OrdinalIgnoreCase.Equals(id, AppleMusicAppId)
        || StringComparer.OrdinalIgnoreCase.Equals(id, "AppleMusic.exe");

    private static bool IsBareExecutableId(string id) => id.Length <= 260
        && id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && StringComparer.Ordinal.Equals(id, id.Trim())
        && !id.Any(character => char.IsControl(character) || character is '\\' or '/' or ':' or '!');
}

/// <summary>
/// Watches playback notifications only; it never sends transport commands or reads audio peaks.
/// A notification invalidates pending decisions immediately, before the bounded census completes.
/// </summary>
public sealed class FavoritePlaybackMonitor : IAsyncDisposable
{
    public const int MaximumSessions = 64;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DiagnosticsLogger? _logger;
    private readonly List<SessionSubscription> _subscriptions = [];
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private FavoritePlaybackSnapshot _snapshot = new(0, [], false);
    private Task? _startTask;
    private Task? _refreshTask;
    private Task? _disposeTask;
    private long _version;
    private bool _refreshRequested;
    private bool _disposed;

    public FavoritePlaybackMonitor(DiagnosticsLogger? logger = null) => _logger = logger;

    public event EventHandler? Changed;

    public FavoritePlaybackSnapshot Snapshot { get { lock (_sync) return _snapshot; } }

    /// <summary>Also fails while a notification is being resolved, rather than accepting stale Playing.</summary>
    public bool IsCurrent(long version)
    {
        lock (_sync) return !_disposed && !_refreshRequested && version == _version && version == _snapshot.Version;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _startTask ??= StartCoreAsync(cancellationToken);
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(linked.Token)
                .ConfigureAwait(false);
            Task? refresh;
            lock (_sync)
            {
                if (_disposed) return;
                linked.Token.ThrowIfCancellationRequested();
                _manager = manager;
                manager.SessionsChanged += SessionsChanged;
                manager.CurrentSessionChanged += CurrentSessionChanged;
                refresh = RequestRefreshLocked();
            }
            if (refresh is not null) await refresh.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested) throw;
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                DetachManagerLocked();
                DetachSessionsLocked();
                _snapshot = new(++_version, [], false);
            }
            _logger?.Warn($"Media playback notifications are unavailable: {exception.Message}");
            PublishChanged();
        }
    }

    private void SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        => RequestRefresh(sender);

    private void CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        => RequestRefresh(sender);

    private void RequestRefresh(GlobalSystemMediaTransportControlsSessionManager sender)
    {
        lock (_sync)
            if (!_disposed && _manager is not null && _manager.Equals(sender)) RequestRefreshLocked();
    }

    private void PlaybackChanged(SessionSubscription subscription)
    {
        lock (_sync)
            if (!_disposed && _subscriptions.Contains(subscription)) RequestRefreshLocked();
    }

    private Task? RequestRefreshLocked()
    {
        if (_disposed || _manager is null) return null;
        _version++;
        _refreshRequested = true;
        if (_refreshTask is null || _refreshTask.IsCompleted)
            _refreshTask = Task.Run(RefreshLoop);
        return _refreshTask;
    }

    private void RefreshLoop()
    {
        while (true)
        {
            long version;
            GlobalSystemMediaTransportControlsSessionManager manager;
            lock (_sync)
            {
                if (_disposed || !_refreshRequested || _manager is null) return;
                _refreshRequested = false;
                version = _version;
                manager = _manager;
            }

            (FavoritePlaybackSession[] Sessions, bool Complete) evidence;
            try { evidence = Collect(manager); }
            catch { evidence = ([], false); }
            lock (_sync)
            {
                if (_disposed) return;
                if (version != _version) continue;
                _snapshot = new(version, evidence.Sessions, evidence.Complete);
            }
            PublishChanged();
            // The request flag and task slot are changed under the same lock. A notification
            // arriving as this worker exits must schedule a new worker rather than be lost.
            lock (_sync)
            {
                if (_refreshRequested) continue;
                _refreshTask = null;
                return;
            }
        }
    }

    private (FavoritePlaybackSession[] Sessions, bool Complete) Collect(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var sessions = manager.GetSessions().Take(MaximumSessions + 1).ToArray();
        if (sessions.Length > MaximumSessions)
        {
            lock (_sync) DetachSessionsLocked();
            return ([], false);
        }

        SessionSubscription[] attached;
        var complete = true;
        lock (_sync)
        {
            if (_disposed) return ([], false);
            foreach (var old in _subscriptions.Where(old => !sessions.Any(session => session.Equals(old.Session))).ToArray())
            {
                _subscriptions.Remove(old);
                old.Detach();
            }
            foreach (var session in sessions)
            {
                if (_subscriptions.Any(old => session.Equals(old.Session))) continue;
                var subscription = new SessionSubscription(session, PlaybackChanged);
                _subscriptions.Add(subscription);
                if (!subscription.Attach())
                {
                    _subscriptions.Remove(subscription);
                    complete = false;
                }
            }
            attached = _subscriptions.ToArray();
        }

        var evidence = new List<FavoritePlaybackSession>(attached.Length);
        foreach (var subscription in attached)
        {
            string id;
            try { id = subscription.Session.SourceAppUserModelId; }
            catch { complete = false; continue; }
            FavoritePlaybackState state;
            try { state = MapPlaybackStatus(subscription.Session.GetPlaybackInfo().PlaybackStatus); }
            catch { state = FavoritePlaybackState.Unknown; }
            evidence.Add(new(id, state));
        }
        return (evidence.ToArray(), complete);
    }

    internal static FavoritePlaybackState MapPlaybackStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
        => status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => FavoritePlaybackState.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused
                or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped
                or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => FavoritePlaybackState.NotPlaying,
            _ => FavoritePlaybackState.Unknown
        };

    private void PublishChanged()
    {
        EventHandler? changed;
        lock (_sync) changed = _disposed ? null : Changed;
        changed?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Task? start;
        Task? refresh;
        lock (_sync)
        {
            _disposed = true;
            _version++;
            _lifetime.Cancel();
            DetachManagerLocked();
            start = _startTask;
            refresh = _refreshTask;
        }
        if (start is not null)
            try { await start.ConfigureAwait(false); } catch (OperationCanceledException) { }
        if (refresh is not null) await refresh.ConfigureAwait(false);
        lock (_sync)
        {
            DetachSessionsLocked();
            _snapshot = new(_version, [], false);
        }
        _lifetime.Dispose();
    }

    private void DetachManagerLocked()
    {
        if (_manager is null) return;
        try { _manager.SessionsChanged -= SessionsChanged; } catch { }
        try { _manager.CurrentSessionChanged -= CurrentSessionChanged; } catch { }
        _manager = null;
    }

    private void DetachSessionsLocked()
    {
        foreach (var subscription in _subscriptions) subscription.Detach();
        _subscriptions.Clear();
    }

    private sealed class SessionSubscription(
        GlobalSystemMediaTransportControlsSession session, Action<SessionSubscription> changed)
    {
        public GlobalSystemMediaTransportControlsSession Session { get; } = session;
        public bool Attach()
        {
            try { Session.PlaybackInfoChanged += OnChanged; return true; }
            catch { return false; }
        }
        public void Detach() { try { Session.PlaybackInfoChanged -= OnChanged; } catch { } }
        private void OnChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => changed(this);
    }
}

using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using WindowsLosslessSwitcher.Abstractions;
using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.Tray.Audio;

/// <summary>
/// Reuses upstream's Apple Music metadata and catalog resolver without its switching coordinator,
/// transport controls, recovery, updater, tier guesses, or local-cache file locking. All other
/// applications explicitly remain unknown. The host must expose catalog estimates as opt-in.
/// </summary>
public sealed class AutomaticRateMonitor : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly ITrackSource _trackSource;
    private readonly Func<MediaSessionSnapshot> _getSessionSnapshot;
    private readonly IFormatResolver _catalogResolver;
    private readonly Func<CancellationToken, Task<bool>>? _refreshMetadata;
    private readonly DiagnosticsLogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private const int CatalogCacheCapacity = 256;
    private readonly Dictionary<string, LinkedListNode<(string Key, int Rate, string Evidence)>> _catalogCache = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, int Rate, string Evidence)> _catalogRecency = new();
    private readonly HashSet<Task> _running = [];
    private PlatformAudioSource? _selectedSource;
    private TrackSnapshot? _latestTrack;
    private CancellationTokenSource? _request;
    private long _selectionRevision;
    private long _generation;
    private bool _contextWasUsable;
    private bool _started;
    private bool _disposed;
    private bool _refreshingMetadata;
    private bool _metadataRefreshFailed;
    private Task? _metadataRefreshTask;

    public AutomaticRateMonitor(DiagnosticsLogger logger, string? storefront = null)
    {
        _logger = logger;
        var trackSource = new AppleMusicTrackSource(logger, readArtwork: false);
        _trackSource = trackSource;
        _getSessionSnapshot = trackSource.GetSessionSnapshot;
        _refreshMetadata = trackSource.RefreshMetadataAsync;
        // Do not share the upstream app's persistent cache/settings. This provider keeps its
        // own bounded per-run metadata cache; it cannot introduce stale disk-cache estimates.
        _catalogResolver = new AppleMusicCatalogResolver(logger, configuredStorefront: storefront, requireExactAlbumMatch: true);
    }

    internal AutomaticRateMonitor(DiagnosticsLogger logger, ITrackSource trackSource,
        Func<MediaSessionSnapshot> getSessionSnapshot, IFormatResolver catalogResolver,
        Func<CancellationToken, Task<bool>>? refreshMetadata = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _trackSource = trackSource ?? throw new ArgumentNullException(nameof(trackSource));
        _getSessionSnapshot = getSessionSnapshot ?? throw new ArgumentNullException(nameof(getSessionSnapshot));
        _catalogResolver = catalogResolver ?? throw new ArgumentNullException(nameof(catalogResolver));
        _refreshMetadata = refreshMetadata;
    }

    public event EventHandler<NativeRateObservation>? RateObserved;

    public bool IsEvidenceCurrent(long generation, string sourceKey, string sessionToken, long selectionRevision)
    {
        lock (_sync) return !_disposed && !_refreshingMetadata && !_metadataRefreshFailed
            && _generation == generation && _selectionRevision == selectionRevision
            && StringComparer.OrdinalIgnoreCase.Equals(_selectedSource?.Identity, sourceKey)
            && StringComparer.Ordinal.Equals(_selectedSource?.SessionToken, sessionToken)
            && IsCurrentPlayableTrack(_selectedSource, _latestTrack);
    }

    /// <summary>Track metadata can remain current while paused or its Windows stream restarts.
    /// This permits display only; endpoint writes must use the stronger IsEvidenceCurrent guard.</summary>
    public bool IsMetadataCurrent(long generation, string sourceKey, string sessionToken, long selectionRevision)
    {
        lock (_sync) return !_disposed && !_refreshingMetadata && !_metadataRefreshFailed
            && _generation == generation && _selectionRevision == selectionRevision
            && StringComparer.OrdinalIgnoreCase.Equals(_selectedSource?.Identity, sourceKey)
            && StringComparer.Ordinal.Equals(_selectedSource?.SessionToken, sessionToken)
            && IsCurrentMetadataTrack(_selectedSource, _latestTrack);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                return;
            _started = true;
            _trackSource.TrackChanged += OnTrackChanged;
        }
        await _trackSource.StartAsync(cancellationToken);
    }

    public void SelectSource(PlatformAudioSource? source, long selectionRevision)
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            var sameSelection = _selectedSource?.Identity == source?.Identity
                && _selectedSource?.SessionToken == source?.SessionToken
                && _selectionRevision == selectionRevision;
            _selectedSource = source;
            _selectionRevision = selectionRevision;
            // Upstream does not raise TrackChanged when its media session disappears or
            // pauses. Recheck every census, including an unchanged audio-session token.
            var usable = IsCurrentMetadataTrack(source, _latestTrack);
            if (sameSelection && usable == _contextWasUsable)
            {
                return;
            }
            _contextWasUsable = usable;
        }
        QueueResolution(CancellationToken.None);
    }

    /// <summary>Retries the current track; useful when the catalog was temporarily unreachable.</summary>
    public Task ResolveCurrentAsync(CancellationToken cancellationToken)
    {
        if (_refreshMetadata is null) return QueueResolution(cancellationToken, bypassCatalogCache: true);
        lock (_sync)
        {
            if (_disposed) return Task.CompletedTask;
            if (_metadataRefreshTask is { IsCompleted: false }) return _metadataRefreshTask;
            _request?.Cancel();
            ++_generation;
            _refreshingMetadata = true;
            var lifetimeToken = _lifetime.Token;
            var task = Task.Run(() => RefreshAndResolveAsync(cancellationToken, lifetimeToken));
            _metadataRefreshTask = task;
            TrackTask(task);
            return task;
        }
    }

    private async Task RefreshAndResolveAsync(CancellationToken cancellationToken, CancellationToken lifetimeToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
        var succeeded = false;
        try { succeeded = await _refreshMetadata!(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception error) { _logger.Warn($"Apple Music metadata refresh failed: {error.Message}"); }
        finally
        {
            lock (_sync)
            {
                _refreshingMetadata = false;
                _metadataRefreshFailed = !succeeded;
                _contextWasUsable = IsCurrentMetadataTrack(_selectedSource, _latestTrack);
            }
        }
        await QueueResolution(linked.Token, bypassCatalogCache: true).ConfigureAwait(false);
    }

    private void OnTrackChanged(object? sender, TrackSnapshot track)
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _latestTrack = track;
            _metadataRefreshFailed = false;
            // The Apple provider publishes its coherent session before this notification.
            // Keep the guard for other providers and overlapping metadata transitions.
            _contextWasUsable = IsCurrentMetadataTrack(_selectedSource, track);
        }
        QueueResolution(CancellationToken.None);
    }

    private Task QueueResolution(CancellationToken cancellationToken, bool bypassCatalogCache = false)
    {
        PlatformAudioSource? source;
        TrackSnapshot? track;
        long revision;
        long generation;
        CancellationTokenSource request;
        lock (_sync)
        {
            if (_disposed || _refreshingMetadata)
                return Task.CompletedTask;
            _request?.Cancel();
            request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            _request = request;
            source = _selectedSource;
            track = _latestTrack;
            if (bypassCatalogCache && track is not null) RemoveCachedTrack(track.UniqueKey);
            revision = _selectionRevision;
            generation = ++_generation;
        }

        if (source is null)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_request, request))
                    _request = null;
            }
            request.Dispose();
            return Task.CompletedTask;
        }

        // Immediately invalidate the previous track's result, before any asynchronous lookup.
        PublishIfCurrent(source, revision, generation, track, null,
            source.SupportsAutomaticRate
                ? "Apple Music catalog lookup pending. The result estimates the highest available lossless asset; it does not measure playback."
                : "Native rate is not exposed by Windows audio-session APIs. Use a manual sample-rate override.");

        // Force asynchronous dispatch so catalog/file/network work cannot block the tray UI.
        var task = Task.Run(() => ResolveAsync(source, revision, generation, track, request, bypassCatalogCache), CancellationToken.None);
        TrackTask(task);
        return task;
    }

    private void TrackTask(Task task)
    {
        lock (_sync) _running.Add(task);
        _ = task.ContinueWith(completed =>
        {
            lock (_sync)
                _running.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task ResolveAsync(PlatformAudioSource source, long revision, long generation,
        TrackSnapshot? track, CancellationTokenSource request, bool bypassCatalogCache)
    {
        try
        {
            if (!source.SupportsAutomaticRate)
                return;
            if (track is null || string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(track.Artist))
            {
                PublishIfCurrent(source, revision, generation, track, null,
                    "Apple Music track metadata is unavailable. Use a manual sample-rate override.");
                return;
            }
            if (!IsCurrentMetadataTrack(source, track))
            {
                PublishIfCurrent(source, revision, generation, track, null,
                    "Apple Music is unavailable or its current track metadata is unconfirmed. Retaining the last confirmed rate.");
                return;
            }

            (int Rate, string Evidence) cached;
            lock (_sync)
            {
                if (!bypassCatalogCache && _catalogCache.TryGetValue(track.UniqueKey, out var cachedNode))
                {
                    _catalogRecency.Remove(cachedNode);
                    _catalogRecency.AddFirst(cachedNode);
                    cached = (cachedNode.Value.Rate, cachedNode.Value.Evidence);
                    PublishIfCurrent(source, revision, generation, track, cached.Rate, cached.Evidence);
                    return;
                }
            }

            var result = await _catalogResolver.ResolveAsync(track, request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            if (result is null || result.Confidence != ResolutionConfidence.Exact || !SampleRates.IsValid(result.SampleRateHz))
            {
                PublishIfCurrent(source, revision, generation, track, null,
                    "No usable Apple Music catalog match. Native playback rate remains unknown; use a manual override.");
                return;
            }

            var evidence = $"Apple Music catalog estimate: {result.SampleRateHz / 1000d:0.###} kHz, "
                + $"highest available lossless variant for '{track.Title}'. "
                + "Actual playback may use another variant or a local file; this is not a measured native stream rate.";
            lock (_sync)
            {
                // A canceled old lookup must not poison a later retry/replay's cache.
                if (_disposed || request.IsCancellationRequested || generation != _generation
                    || revision != _selectionRevision || source.Identity != _selectedSource?.Identity
                    || source.SessionToken != _selectedSource.SessionToken || track.UniqueKey != _latestTrack?.UniqueKey) return;
                RemoveCachedTrack(track.UniqueKey);
                if (_catalogCache.Count >= CatalogCacheCapacity)
                    RemoveCachedTrack(_catalogRecency.Last!.Value.Key);
                var node = _catalogRecency.AddFirst((track.UniqueKey, result.SampleRateHz, evidence));
                _catalogCache.Add(track.UniqueKey, node);
            }
            PublishIfCurrent(source, revision, generation, track, result.SampleRateHz, evidence);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            // Selection, track, or session changed. Old observations must not touch the endpoint.
        }
        catch (Exception ex)
        {
            _logger.Warn($"System-wide catalog metadata lookup failed: {ex.Message}");
            PublishIfCurrent(source, revision, generation, track, null,
                "Apple Music catalog lookup failed. Retaining the current rate; manual overrides remain available.");
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_request, request))
                    _request = null;
            }
            request.Dispose();
        }
    }

    // Called only under _sync; retry invalidation and LRU eviction must update both indexes.
    private void RemoveCachedTrack(string key)
    {
        if (_catalogCache.Remove(key, out var node)) _catalogRecency.Remove(node);
    }

    private void PublishIfCurrent(PlatformAudioSource source, long revision, long generation,
        TrackSnapshot? track, int? rate, string evidence)
    {
        lock (_sync)
        {
            if (_disposed || generation != _generation || revision != _selectionRevision
                || source.Identity != _selectedSource?.Identity || source.SessionToken != _selectedSource.SessionToken
                || track?.UniqueKey != _latestTrack?.UniqueKey)
                return;
            // A lookup can finish after GSMTC stopped or detached without a track-change
            // callback. Never publish that old result against a new/current audio session.
            if (rate is not null && !IsCurrentMetadataTrack(_selectedSource, track))
            {
                rate = null;
                evidence = "Apple Music's playback or metadata changed during lookup. Retaining the last confirmed rate.";
            }
            RateObserved?.Invoke(this, new(source.Identity, source.SessionToken, revision,
                generation, track?.UniqueKey, rate, evidence));
        }
    }

    private bool IsCurrentPlayableTrack(PlatformAudioSource? source, TrackSnapshot? track)
        => CanUseCatalogTrack(source, track, _getSessionSnapshot());

    private bool IsCurrentMetadataTrack(PlatformAudioSource? source, TrackSnapshot? track)
        => !_metadataRefreshFailed && CanUseCatalogMetadataTrack(source, track, _getSessionSnapshot());

    // Pure guard shared by lookup dispatch and completion; tests do not create Windows
    // media sessions, inspect processes, or read/write an endpoint.
    internal static bool CanUseCatalogTrack(PlatformAudioSource? source, TrackSnapshot? track,
        MediaSessionSnapshot session)
        => source?.IsActive == true && session.PlaybackStatus == MediaTransportPlaybackStatus.Playing
            && CanUseCatalogMetadataTrack(source, track, session);

    internal static bool CanUseCatalogMetadataTrack(PlatformAudioSource? source, TrackSnapshot? track,
        MediaSessionSnapshot session)
    {
        if (source is null || !source.SupportsAutomaticRate || track is null
            || string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(track.Artist))
            return false;

        return session.PlaybackStatus is MediaTransportPlaybackStatus.Playing or MediaTransportPlaybackStatus.Paused
            && !string.IsNullOrWhiteSpace(session.SourceAppUserModelId)
            && StringComparer.Ordinal.Equals(session.SourceAppUserModelId, track.SourceAppUserModelId)
            && StringComparer.Ordinal.Equals(session.Title, track.Title)
            && StringComparer.Ordinal.Equals(session.Artist, track.Artist)
            && StringComparer.Ordinal.Equals(session.Album, track.Album);
    }

    public async ValueTask DisposeAsync()
    {
        Task[] running;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _generation++;
            _trackSource.TrackChanged -= OnTrackChanged;
            _lifetime.Cancel();
            running = _running.ToArray();
        }
        await _trackSource.DisposeAsync();
        await Task.WhenAll(running).ConfigureAwait(false);
        _lifetime.Dispose();
    }
}

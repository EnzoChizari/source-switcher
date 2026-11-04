using System.ComponentModel;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.Tray.Audio;

/// <summary>Observes format metadata only. It never captures samples, injects code, or changes a format.</summary>
internal sealed class WasapiStreamRateMonitor : IAsyncDisposable
{
    private readonly object _sync = new();
    // Flush and dispose share a lifetime lock, independent of the tracker lock: the
    // reader must remain free to process callbacks while ControlTrace flushes/stops.
    private readonly object _sessionLifetime = new();
    private readonly StreamRateTracker _tracker = new();
    private readonly ProcessIdentityCache _processes = new();
    private TraceEventSession? _session;
    private Task _reader = Task.CompletedTask;
    private bool _running;
    private bool _automaticRetryAllowed = true;
    private bool _disposed;
    private long _eventsLost;
    private long _version;
    private DateTimeOffset? _lastEventTimeUtc;
    private string? _error;
    private AudioServiceStreamSnapshotResult? _serviceSnapshot;
    private bool _serviceDirty = true, _serviceObserved;
    private long _transitionVersion;

    /// <summary>Signals changed evidence. Subscribers should enqueue work and return promptly.</summary>
    public event Action? Changed;
    public string? Error { get { lock (_sync) return _error ?? _serviceSnapshot?.Error; } }
    public bool IsRunning { get { lock (_sync) return _running; } }
    public bool CanRetryAutomatically { get { lock (_sync) return _automaticRetryAllowed; } }
    public long Version { get { lock (_sync) return _version; } }
    public DateTimeOffset? LastEventTimeUtc { get { lock (_sync) return _lastEventTimeUtc; } }
    public bool NeedsExistingStreamRefresh { get { lock (_sync) return _running && _serviceDirty; } }

    /// <summary>Requests a fresh service census without discarding the live Client listener.</summary>
    public void InvalidateExistingStreams()
    {
        lock (_sync) { _serviceDirty = true; _transitionVersion++; _version++; }
    }

    public bool IsEvidenceCurrent(long version)
    { lock (_sync) return _running && !_serviceDirty && _version == version && _serviceSnapshot?.Error is null; }

    /// <summary>
    /// Runs on the refresh worker. A separate, short lived Service trace enumerates
    /// already-open streams; stop and consumer drain form the census completion barrier.
    /// The live Client trace stays attached throughout, detecting intervening changes.
    /// </summary>
    public void RefreshExistingStreams()
    {
        long version;
        lock (_sync)
        {
            if (_disposed || !_running || !_serviceDirty) return;
            version = _transitionVersion;
        }
        var snapshot = AudioServiceStreamSnapshot.Read();
        lock (_sync)
        {
            if (_disposed || !_running) return;
            if (version != _transitionVersion)
            {
                // A transition during the census can make its first rows stale.
                _serviceDirty = true;
            }
            else
            {
                _serviceSnapshot = snapshot;
                _serviceObserved |= snapshot.WasObserved;
                _serviceDirty = false;
                if (snapshot.Error is null) _error = null;
                _version++;
            }
        }
        // Retry a raced census through the normal coalesced refresh path.
        NotifyChanged();
    }

    public void Start()
    {
        lock (_sessionLifetime)
        lock (_sync)
        {
            if (_session is not null || _disposed) return;
            try
            {
                _session = new TraceEventSession($"DX5SourceSwitcher.StreamRates.{Environment.ProcessId}",
                    TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate)
                { StopOnDispose = true, BufferSizeMB = 4 };
                var source = _session.Source;
                source.Dynamic.All += OnEvent;
                // Keep only Client events attached continuously. Service rundown uses a
                // separate brief trace; high-volume buffer providers are never enabled.
                _session.EnableProvider(WasapiStreamEventParser.ProviderId, TraceEventLevel.Verbose, ulong.MaxValue);
                _running = true;
                _error = null;
                _reader = Task.Factory.StartNew(() =>
                {
                    try { source.Process(); }
                    catch (Exception ex)
                    {
                        lock (_sync)
                        {
                            _automaticRetryAllowed = !IsAccessDenied(ex);
                            _error = "Audio tracing stopped: " + ex.Message;
                        }
                    }
                    finally
                    {
                        lock (_sync)
                        {
                            if (_session is not null && _error is null) _error = "Audio tracing stopped; automatic recovery pending.";
                            _running = false;
                            _tracker.Clear();
                            _serviceSnapshot = null;
                            _serviceDirty = true;
                            _version++;
                        }
                        NotifyChanged();
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                _session?.Dispose();
                _session = null;
                _running = false;
                _automaticRetryAllowed = !IsAccessDenied(ex);
                _error = !_automaticRetryAllowed
                    ? "Automatic tracing needs permission. Exit and run this app as administrator."
                    : "Automatic audio tracing is unavailable: " + ex.Message;
            }
            _version++;
        }
        NotifyChanged();
    }

    internal static bool IsAccessDenied(Exception error) => error is UnauthorizedAccessException
        || error.HResult == unchecked((int)0x80070005) || error is Win32Exception { NativeErrorCode: 5 };

    /// <summary>
    /// Delivers a partial ETW buffer after a Core Audio session notification. This is
    /// event driven; an idle monitor has no periodic high-frequency flush timer.
    /// </summary>
    public void FlushPendingEvents()
    {
        var changed = false;
        lock (_sessionLifetime)
        {
            TraceEventSession? session;
            lock (_sync) session = _running ? _session : null;
            if (session is null) return;
            try
            {
                session.Flush();
                // Real-time source headers do not refresh their loss counters. Query
                // the running controller while its lifetime lock prevents disposal.
                var lost = session.EventsLost;
                lock (_sync)
                {
                    if (_session == session && lost != _eventsLost)
                    {
                        _eventsLost = lost;
                        _tracker.Clear();
                        _serviceDirty = true;
                        _transitionVersion++;
                        _version++;
                        changed = true;
                    }
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                lock (_sync)
                {
                    if (_session != session) return;
                    _tracker.Clear();
                    _serviceDirty = true;
                    _transitionVersion++;
                    _error = "Audio trace flush failed; waiting for fresh streams: " + ex.Message;
                    _version++;
                    changed = true;
                }
            }
        }
        if (changed) NotifyChanged();
    }

    private void OnEvent(TraceEvent e)
    {
        if (e.ProviderGuid != WasapiStreamEventParser.ProviderId
            || e.EventName is not ("AudioClientInitialize" or "AudioClientStart" or "AudioClientStop")) return;
        var changed = false;
        try
        {
            // Read only the fields actually requested by the strict parser. Start/stop
            // need two fields; initialization needs ten. Unrelated payloads stay unboxed.
            var values = new TracePayloadValues(e.PayloadNames, e.PayloadValue);
            var signal = WasapiStreamEventParser.Parse(e.ProviderGuid, e.EventName, e.ProcessID,
                new DateTimeOffset(e.TimeStamp.ToUniversalTime()), values);
            // Start/stop for a client initialized before this listener began still
            // invalidate a Service census, even though the Client tracker has no GUID.
            if (!values.IsFailedCall())
            {
                var owner = _processes.Read(e.ProcessID);
                if (owner is not null && SourceExclusions.IsExcluded(owner.ExecutablePath)) return;
                lock (_sync)
                {
                    if (_disposed) return;
                    if (_serviceSnapshot is null || e.TimeStamp.ToUniversalTime() >= _serviceSnapshot.StartedAtUtc.UtcDateTime)
                    {
                        _serviceDirty = true;
                        _transitionVersion++;
                        _version++;
                        changed = true;
                    }
                }
            }
            if (signal is null)
            {
                // A failed API call did not change playback state. A malformed successful
                // start/stop could hide a transition, so invalidate evidence conservatively.
                if (e.EventName is ("AudioClientStart" or "AudioClientStop") && !values.IsFailedCall())
                {
                    var process = _processes.Read(e.ProcessID);
                    if (process is not null && SourceExclusions.IsExcluded(process.ExecutablePath)) return;
                    lock (_sync) { _tracker.Clear(); _version++; }
                    changed = true;
                }
            }
            else if (signal.Kind == StreamSignalKind.Initialize)
            {
                var process = _processes.Read(signal.ProcessId);
                if (process is null || process.Identity.StartedAtUtc > signal.TimestampUtc
                    || SourceExclusions.IsExcluded(process.ExecutablePath)) return;
                lock (_sync)
                {
                    if (_disposed) return;
                    var initialized = _tracker.Initialize(new(signal.ProcessId, process.Identity.StartedAtUtc, signal.ClientId,
                        signal.EndpointId!, signal.SampleRateHz!.Value, signal.TimestampUtc));
                    changed |= initialized;
                    if (initialized) { _lastEventTimeUtc = signal.TimestampUtc; _version++; _error = null; }
                }
            }
            else
            {
                lock (_sync)
                {
                    if (_disposed) return;
                    var updated = _tracker.SetPlaying(signal.ProcessId, signal.ClientId,
                        signal.Kind == StreamSignalKind.Start, signal.TimestampUtc);
                    changed |= updated;
                    if (updated) { _lastEventTimeUtc = signal.TimestampUtc; _version++; }
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
        {
            // Unexpected schemas cannot turn a guessed field into a rate. Drop all evidence
            // if an event could not be decoded so a missed stop cannot leave a stale stream.
            lock (_sync)
            {
                _tracker.Clear();
                _serviceDirty = true;
                _transitionVersion++;
                _error = "An audio trace event could not be decoded; waiting for fresh streams.";
                _version++;
            }
            changed = true;
        }
        if (changed) NotifyChanged();
    }

    public StreamRateResult Resolve(string endpointId, IReadOnlyList<int> processIds, string executablePath,
        IReadOnlyList<string>? sessionInstanceIds = null, IReadOnlyDictionary<int, string>? processExecutablePaths = null)
    {
        if (SourceExclusions.IsExcluded(executablePath)) return new(null, "This source is excluded from detection.");
        // The retained process handle proves this is still the same process instance.
        // Creation time and image path were read once, rather than reopening modules per row.
        var identities = new List<StreamProcessIdentity>(processIds.Count);
        foreach (var pid in processIds)
        {
            var actualPath = processExecutablePaths?.GetValueOrDefault(pid) ?? executablePath;
            // A logical Apple Music row may contain its official package's playback
            // helper. Every PID must still match its real executable and package root.
            if (!StringComparer.OrdinalIgnoreCase.Equals(actualPath, executablePath)
                && !AppleMusicIdentity.Equivalent(actualPath, executablePath)) continue;
            var process = _processes.Read(pid, actualPath);
            if (process is not null) identities.Add(process.Identity);
        }

        StreamRateResult result;
        lock (_sync)
        {
            if (!_running) return new(null, _error ?? "Automatic audio tracing is stopped.");
            if (_serviceDirty)
                result = new(null, "Checking existing Windows audio streams…");
            else if (_serviceSnapshot?.Error is { } snapshotError)
                result = new(null, snapshotError);
            else if (_serviceObserved && _serviceSnapshot is { } snapshot)
                result = ResolveServiceSnapshot(snapshot, endpointId, identities, sessionInstanceIds);
            else result = _tracker.Resolve(endpointId, identities);
        }
        return result;
    }

    internal static StreamRateResult ResolveServiceSnapshot(AudioServiceStreamSnapshotResult snapshot,
        string endpointId, IReadOnlyList<StreamProcessIdentity> processes, IReadOnlyList<string>? sessionInstanceIds = null)
    {
        if (snapshot.Error is { } error) return new(null, error);
        if (processes.Any(process => snapshot.InvalidProcessIds.Contains(process.ProcessId)))
            return new(null, "Windows reported an unsupported or incomplete stream format for this source.");
        var rates = snapshot.Streams.Where(stream => stream.IsRunning
            && StringComparer.OrdinalIgnoreCase.Equals(stream.EndpointId, endpointId)
            && processes.Any(process => process.ProcessId == stream.SourceProcessId
                && process.StartedAtUtc <= stream.TimestampUtc)
            && (sessionInstanceIds is null || sessionInstanceIds.Contains(stream.SessionInstanceId, StringComparer.Ordinal)))
            .Select(stream => stream.SampleRateHz).Distinct().Take(2).ToArray();
        return rates.Length switch
        {
            0 => new(null, "No running shared PCM stream in the current Windows Audio Service census."),
            1 => new(rates[0], "Current Windows Audio Service stream format; already-open streams are included."),
            _ => new(null, "Playing streams use conflicting rates; retaining the current output rate.")
        };
    }

    private void NotifyChanged()
    {
        // A notification subscriber cannot terminate the ETW processing thread. Events
        // are advisory; the next session census still reads the current tracker state.
        try { Changed?.Invoke(); } catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        TraceEventSession? session;
        lock (_sessionLifetime)
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            session = _session;
            _session = null;
            _running = false;
            _tracker.Clear();
            _serviceSnapshot = null;
            _version++;
        }
        try
        {
            if (session is not null)
            {
                // Stop outside the tracker lock: the reader can be handling a final event.
                await Task.Run(() =>
                {
                    lock (_sessionLifetime)
                    { try { session.Stop(noThrow: true); } finally { session.Dispose(); } }
                });
                await _reader;
            }
        }
        finally { _processes.Dispose(); }
    }
}

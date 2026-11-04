using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace WindowsSourceSwitcher.Tray.Audio;

/// <summary>
/// Keeps notifications for a single endpoint alive. COM callbacks only enqueue work; session
/// registration and unregistration run on an idle MTA owner thread, outside those callbacks.
/// </summary>
public sealed class AudioSessionWatcher : IDisposable
{
    private readonly string _endpointId;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ConcurrentQueue<IAudioSessionControl> _created = new();
    private readonly Dictionary<string, Subscription> _sessions = new(StringComparer.Ordinal);
    private readonly Thread _thread;
    private int _stop, _reconcile, _changed, _volumeChanged, _ready;
    private string? _lastError;

    /// <summary>Raised on the MTA worker. Handlers must marshal UI work and return promptly.</summary>
    public event Action? Changed;
    public event Action? VolumeChanged;

    public string? LastError => Volatile.Read(ref _lastError);
    public bool IsReady => Volatile.Read(ref _ready) != 0;

    public AudioSessionWatcher(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        _endpointId = endpointId;
        _thread = new Thread(Run) { IsBackground = true, Name = "DX5 audio session notifications" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Schedules a fallback rescan without blocking the caller.</summary>
    public void RefreshSubscriptions()
    {
        if (Volatile.Read(ref _stop) != 0) return;
        Interlocked.Exchange(ref _reconcile, 1);
        Wake();
    }

    private void Run()
    {
        MMDeviceEnumerator? enumerator = null;
        MMDevice? device = null;
        AudioSessionManager? manager = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDevice(_endpointId);
            manager = device.AudioSessionManager;
            manager.OnSessionCreated += SessionCreated;
            SubscribeExisting(manager);
            Volatile.Write(ref _ready, 1);
            NotifyChanged();

            while (Volatile.Read(ref _stop) == 0)
            {
                _wake.WaitOne();
                if (Volatile.Read(ref _stop) != 0) break;
                while (_created.TryDequeue(out var created))
                {
                    try { Subscribe(new AudioSessionControl(created)); }
                    catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException) { }
                }

                if (Interlocked.Exchange(ref _reconcile, 0) != 0)
                {
                    manager.RefreshSessions();
                    SubscribeExisting(manager);
                }
                RemoveExpired();
                if (Interlocked.Exchange(ref _changed, 0) != 0)
                    NotifyChanged();
                if (Interlocked.Exchange(ref _volumeChanged, 0) != 0 && Volatile.Read(ref _stop) == 0)
                {
                    try { VolumeChanged?.Invoke(); }
                    catch (InvalidOperationException) { }
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            Volatile.Write(ref _lastError, "Audio session notifications are temporarily unavailable: " + ex.Message);
            NotifyChanged();
        }
        finally
        {
            Volatile.Write(ref _ready, 0);
            if (manager is not null)
                manager.OnSessionCreated -= SessionCreated;
            foreach (var subscription in _sessions.Values)
                TryDispose(subscription.Session, subscription.Session.Dispose);
            _sessions.Clear();
            // MMDevice.Dispose also disposes its cached manager. Suppress the manager's own
            // finalizer even if Windows has removed the endpoint and unregistering fails;
            // NAudio's finalizer otherwise retries the same HRESULT-throwing operation.
            if (device is not null) TryDispose(device, device.Dispose);
            if (manager is not null) GC.SuppressFinalize(manager);
            if (enumerator is not null) TryDispose(enumerator, enumerator.Dispose);
            while (_created.TryDequeue(out _)) { }
            _wake.Dispose();
        }
    }

    private void SubscribeExisting(AudioSessionManager manager)
    {
        var sessions = manager.Sessions;
        // Reading Count also primes Windows session-created notification delivery.
        var count = sessions.Count;
        for (var index = 0; index < count; index++)
        {
            try { Subscribe(sessions[index]); }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
            {
                // A session can vanish between enumeration and subscription; a future change
                // or the caller's fallback refresh repairs the set.
            }
        }
    }

    private void Subscribe(AudioSessionControl session)
    {
        var retained = false;
        try
        {
            if (session.State == AudioSessionState.AudioSessionStateExpired) return;
            var key = session.GetSessionInstanceIdentifier;
            if (_sessions.ContainsKey(key)) return;
            var events = new SessionEvents(SignalChanged, SignalVolumeChanged);
            session.RegisterEventClient(events);
            _sessions.Add(key, new(session, events));
            retained = true;
        }
        finally
        {
            if (!retained) TryDispose(session, session.Dispose);
        }
    }

    private void RemoveExpired()
    {
        foreach (var pair in _sessions.ToArray())
        {
            bool expired;
            try { expired = pair.Value.Session.State == AudioSessionState.AudioSessionStateExpired; }
            catch (Exception ex) when (ex is COMException or InvalidOperationException) { expired = true; }
            if (!expired) continue;
            _sessions.Remove(pair.Key);
            TryDispose(pair.Value.Session, pair.Value.Session.Dispose);
        }
    }

    private void SessionCreated(object sender, IAudioSessionControl session)
    {
        if (Volatile.Read(ref _stop) != 0) return;
        _created.Enqueue(session);
        SignalChanged();
    }

    private void SignalChanged()
    {
        if (Volatile.Read(ref _stop) != 0) return;
        Interlocked.Exchange(ref _changed, 1);
        Wake();
    }

    private void Wake()
    {
        try { _wake.Set(); }
        catch (ObjectDisposedException) { }
    }

    private void SignalVolumeChanged()
    {
        if (Volatile.Read(ref _stop) != 0) return;
        Interlocked.Exchange(ref _volumeChanged, 1);
        Wake();
    }

    private void NotifyChanged()
    {
        if (Volatile.Read(ref _stop) != 0) return;
        try { Changed?.Invoke(); }
        catch (InvalidOperationException) { /* A UI handle may have closed during shutdown. */ }
    }

    private static void TryDispose(object target, Action dispose)
    {
        try { dispose(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
        finally { GC.SuppressFinalize(target); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stop, 1) != 0) return;
        Wake();
        // Never wait from a COM callback. Only this worker raises our public event; a caller
        // disposing inside it can let Run's finally finish without joining itself.
        if (Thread.CurrentThread == _thread) return;
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private sealed record Subscription(AudioSessionControl Session, SessionEvents Events);

    private sealed class SessionEvents(Action changed, Action volumeChanged) : IAudioSessionEventsHandler
    {
        public void OnStateChanged(AudioSessionState state) => changed();
        public void OnSessionDisconnected(AudioSessionDisconnectReason reason) => changed();
        public void OnDisplayNameChanged(string displayName) { }
        public void OnIconPathChanged(string iconPath) { }
        public void OnVolumeChanged(float volume, bool isMuted) => volumeChanged();
        public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex) { }
        public void OnGroupingParamChanged(ref Guid groupingId) { }
    }
}

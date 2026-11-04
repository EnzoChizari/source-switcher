using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace WindowsSourceSwitcher.Tray.Audio;

public enum AudioEndpointChangeKind
{
    Added,
    Removed,
    StateChanged,
    DefaultChanged,
    PropertyChanged
}

public sealed record AudioEndpointChange(AudioEndpointChangeKind Kind, string? EndpointId);

/// <summary>
/// Retains endpoint notifications on a dedicated MTA owner thread. Native callbacks only queue
/// immutable notifications; enumerator registration, event delivery and cleanup happen on the owner.
/// This class never queries an endpoint, changes the default device, or writes audio settings.
/// </summary>
public sealed class AudioDeviceWatcher : IDisposable
{
    private const int MaxPendingChanges = 256;
    private readonly object _gate = new();
    private readonly HashSet<AudioEndpointChange> _pending = [];
    private readonly AutoResetEvent _wake = new(false);
    private readonly Func<IAudioDeviceNotificationRegistration> _createRegistration;
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private int _stop, _ready;
    private bool _overflow;
    private string? _lastError;

    /// <summary>
    /// Raised off the UI thread. Handlers must marshal UI work and return promptly. Duplicate
    /// queued notifications may be coalesced. A null endpoint requests a conservative full refresh;
    /// it can represent a cleared default endpoint or a bounded-queue overflow.
    /// </summary>
    public event Action<AudioEndpointChange>? Changed;

    public bool IsReady => Volatile.Read(ref _ready) != 0;
    public string? LastError => Volatile.Read(ref _lastError);
    public Task Completion => _completion.Task;

    public AudioDeviceWatcher() : this(static () => new NativeAudioDeviceNotificationRegistration()) { }

    internal AudioDeviceWatcher(Func<IAudioDeviceNotificationRegistration> createRegistration)
    {
        _createRegistration = createRegistration ?? throw new ArgumentNullException(nameof(createRegistration));
        _thread = new Thread(Run) { IsBackground = true, Name = "DX5 audio endpoint notifications" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void Run()
    {
        IAudioDeviceNotificationRegistration? registration = null;
        var notifications = new EndpointNotifications(Enqueue);
        var registered = false;
        try
        {
            if (Volatile.Read(ref _stop) != 0) return;
            registration = _createRegistration()
                ?? throw new InvalidOperationException("The endpoint notification registration is unavailable.");
            if (Volatile.Read(ref _stop) != 0) return;
            registration.Register(notifications);
            registered = true;
            if (Volatile.Read(ref _stop) == 0)
                Volatile.Write(ref _ready, 1);

            while (Volatile.Read(ref _stop) == 0)
            {
                _wake.WaitOne();
                if (Volatile.Read(ref _stop) != 0) break;
                foreach (var change in TakePending())
                {
                    if (Volatile.Read(ref _stop) != 0) break;
                    Publish(change);
                }
            }
        }
        catch (Exception exception)
        {
            RecordError("Audio endpoint notifications are unavailable", exception);
        }
        finally
        {
            Interlocked.Exchange(ref _stop, 1);
            Volatile.Write(ref _ready, 0);
            if (registered && registration is not null)
            {
                try { registration.Unregister(notifications); }
                catch (Exception exception) { RecordError("Audio endpoint notification removal failed", exception); }
            }
            if (registration is not null)
            {
                try { registration.Dispose(); }
                catch (Exception exception) { RecordError("Audio endpoint notification cleanup failed", exception); }
            }
            lock (_gate)
            {
                _pending.Clear();
                _overflow = false;
            }
            _wake.Dispose();
            _completion.TrySetResult(true);
        }
    }

    private void Enqueue(AudioEndpointChange change)
    {
        // This is the only work performed by the native IMMNotificationClient callbacks.
        if (Volatile.Read(ref _stop) != 0) return;
        lock (_gate)
        {
            if (Volatile.Read(ref _stop) != 0) return;
            if (_pending.Count < MaxPendingChanges || _pending.Contains(change))
                _pending.Add(change);
            else
                _overflow = true;
        }
        Wake();
    }

    private AudioEndpointChange[] TakePending()
    {
        lock (_gate)
        {
            var changes = _pending.ToList();
            _pending.Clear();
            if (_overflow)
            {
                // Endpoint identity was lost during an unusual notification burst. The consumer
                // must refresh its current pinned endpoint rather than infer which device changed.
                changes.Add(new(AudioEndpointChangeKind.PropertyChanged, null));
                _overflow = false;
            }
            return changes.ToArray();
        }
    }

    private void Publish(AudioEndpointChange change)
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (Action<AudioEndpointChange> handler in handlers.GetInvocationList())
        {
            if (Volatile.Read(ref _stop) != 0) return;
            try { handler(change); }
            catch (Exception exception) { RecordError("Audio endpoint notification handler failed", exception); }
        }
    }

    private void RecordError(string operation, Exception exception)
    {
        var detail = exception.Message;
        if (detail.Length > 1024) detail = detail[..1024];
        Volatile.Write(ref _lastError, operation + ": " + detail);
    }

    private void Wake()
    {
        try { _wake.Set(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stop, 1) != 0) return;
        Volatile.Write(ref _ready, 0);
        Wake();
        // A slow COM unregister or consumer callback must not indefinitely block app shutdown.
        // Completion signals eventual owner-thread cleanup if this bounded join expires.
        if (Thread.CurrentThread != _thread)
            _thread.Join(TimeSpan.FromSeconds(2));
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class EndpointNotifications(Action<AudioEndpointChange> enqueue) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) =>
            enqueue(new(AudioEndpointChangeKind.StateChanged, deviceId));
        public void OnDeviceAdded(string deviceId) => enqueue(new(AudioEndpointChangeKind.Added, deviceId));
        public void OnDeviceRemoved(string deviceId) => enqueue(new(AudioEndpointChangeKind.Removed, deviceId));
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) =>
            enqueue(new(AudioEndpointChangeKind.DefaultChanged, defaultDeviceId));
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) =>
            enqueue(new(AudioEndpointChangeKind.PropertyChanged, deviceId));
    }
}

internal interface IAudioDeviceNotificationRegistration : IDisposable
{
    void Register(IMMNotificationClient notifications);
    void Unregister(IMMNotificationClient notifications);
}

internal sealed class NativeAudioDeviceNotificationRegistration : IAudioDeviceNotificationRegistration
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private bool _disposed;

    public void Register(IMMNotificationClient notifications) =>
        Marshal.ThrowExceptionForHR(_enumerator.RegisterEndpointNotificationCallback(notifications));

    public void Unregister(IMMNotificationClient notifications) =>
        Marshal.ThrowExceptionForHR(_enumerator.UnregisterEndpointNotificationCallback(notifications));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enumerator.Dispose();
    }
}

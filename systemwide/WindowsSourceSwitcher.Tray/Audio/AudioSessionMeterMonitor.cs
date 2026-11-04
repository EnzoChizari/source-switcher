using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi.Interfaces;
using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.Tray.Audio;

/// <summary>A Windows-reported session peak. Null means unavailable, rather than silence.</summary>
public sealed record AudioSourcePeak(
    string Identity, IReadOnlyList<VolumeSessionIdentity> Sessions, float? Peak, string? Error);

/// <summary>SampledAtTimestamp uses Stopwatch.GetTimestamp, not wall-clock time.</summary>
public sealed record AudioMeterFrame(
    string EndpointId, long Generation, long Sequence, long SampledAtTimestamp,
    IReadOnlyList<AudioSourcePeak> Sources, string? Error);

/// <summary>
/// Reads session peak meters on one MTA owner thread, only while the mixer is visible.
/// The caller supplies its existing low-frequency session census; polling never enumerates
/// sessions, captures audio, or changes volume, mute, endpoint format, or source selection.
/// </summary>
public sealed class AudioSessionMeterMonitor : IDisposable
{
    internal const int MaximumSources = 256;
    internal const int MaximumSessions = 256;
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly Action<AudioMeterFrame> _onFrame;
    private readonly Func<IAudioSessionMeterStore> _openStore;
    private readonly int _intervalMilliseconds;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private MeterRequest? _pending;
    private long _generation;
    private bool _hasPending, _disposed;

    /// <summary>Completes after the MTA worker has released its native resources.</summary>
    public Task Completion => _completion.Task;

    public AudioSessionMeterMonitor(Action<AudioMeterFrame> onFrame)
        : this(onFrame, () => new NativeAudioSessionMeterStore(), 50) { }

    internal AudioSessionMeterMonitor(Action<AudioMeterFrame> onFrame,
        Func<IAudioSessionMeterStore> openStore, int intervalMilliseconds = 50)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        ArgumentNullException.ThrowIfNull(openStore);
        if (intervalMilliseconds is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds));
        _onFrame = onFrame;
        _openStore = openStore;
        _intervalMilliseconds = intervalMilliseconds;
        _thread = new Thread(Run) { IsBackground = true, Name = "DX5 session peak meters" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>
    /// Reconciles cached meter leases from a fresh census and returns a new generation.
    /// Ignore queued frames from older generations. Passing null or an empty census stops
    /// polling and releases every endpoint/session lease on its owning worker thread.
    /// </summary>
    public long SetSources(AudioSessionVolumeCensus? census)
    {
        var request = MeterRequest.From(census);
        lock (_gate)
        {
            if (_disposed) return _generation;
            _pending = request;
            _hasPending = true;
            _generation++;
            _wake.Set();
            return _generation;
        }
    }

    public void Stop() => SetSources(null);

    private void Run()
    {
        AudioSessionMeterSampler? sampler = null;
        MeterRequest? currentRequest = null;
        long currentGeneration = 0, sequence = 0;
        var polling = false;
        string? fault = null;
        try
        {
            while (true)
            {
                var cycleStartedAt = Stopwatch.GetTimestamp();
                MeterRequest? request = null;
                bool reconcile;
                lock (_gate)
                {
                    if (_disposed) break;
                    reconcile = _hasPending;
                    if (reconcile)
                    {
                        request = _pending;
                        currentGeneration = _generation;
                        _hasPending = false;
                    }
                }
                if (reconcile)
                {
                    currentRequest = request;
                    polling = request is not null;
                    fault = null;
                    try
                    {
                        // Creation, binding, reads and release stay on this MTA thread.
                        // A failed store is retried only on the next supplied census.
                        if (request is null) ReleaseSampler(ref sampler);
                        else
                        {
                            sampler ??= new AudioSessionMeterSampler(_openStore());
                            sampler.SetSources(request);
                        }
                    }
                    catch (Exception ex)
                    {
                        fault = "Session peaks are unavailable: " + ex.Message;
                        ReleaseSampler(ref sampler);
                    }
                }
                if (polling)
                {
                    AudioMeterFrame frame;
                    try
                    {
                        frame = sampler is not null ? sampler.Sample(currentGeneration, ++sequence, Stopwatch.GetTimestamp())
                            : UnavailableFrame(currentRequest!, currentGeneration, ++sequence, fault!);
                    }
                    catch (Exception ex)
                    {
                        fault = "Session peaks are unavailable: " + ex.Message;
                        ReleaseSampler(ref sampler);
                        frame = UnavailableFrame(currentRequest!, currentGeneration, ++sequence, fault);
                    }
                    // Serialize delivery with Stop/SetSources. A slow read cannot publish
                    // its obsolete endpoint/session frame after a newer request arrives.
                    lock (_gate)
                    {
                        if (!_disposed && !_hasPending && currentGeneration == _generation)
                        {
                            try { _onFrame(frame); }
                            catch (Exception) { /* A consumer failure must never terminate the audio controller. */ }
                        }
                    }
                    // Publish an explicit unavailable frame once, then wait for a fresh
                    // census before retrying a faulted backend. No busy failure loop.
                    if (sampler is null) polling = false;
                }
                // The configured interval is the entire sampling period, including COM
                // reads and delivery. Always yield at least 1 ms if Windows is slow;
                // never busy-spin or issue a catch-up burst of native calls.
                var waitMilliseconds = polling ? Math.Max(1, (int)Math.Ceiling(
                    _intervalMilliseconds - Stopwatch.GetElapsedTime(cycleStartedAt).TotalMilliseconds)) : Timeout.Infinite;
                _wake.WaitOne(waitMilliseconds);
            }
        }
        finally
        {
            ReleaseSampler(ref sampler);
            _wake.Dispose();
            _completion.TrySetResult();
        }
    }

    private static AudioMeterFrame UnavailableFrame(MeterRequest request, long generation, long sequence, string error) =>
        new(request.EndpointId, generation, sequence, Stopwatch.GetTimestamp(),
            Array.AsReadOnly(request.Sources.Select(source => new AudioSourcePeak(source.Identity, source.Sessions, null, error)).ToArray()), error);

    private static void ReleaseSampler(ref AudioSessionMeterSampler? sampler)
    {
        try { sampler?.Dispose(); }
        catch (Exception) { /* Best-effort cleanup also stays on the COM owner thread. */ }
        finally { sampler = null; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                _generation++;
                _wake.Set();
            }
        }
        // A callback can stop its own monitor. In that case Run's finally owns cleanup.
        // COM reads cannot be forcibly cancelled if a driver/service stalls. Bound the
        // synchronous wait; Completion confirms eventual owner-thread resource release.
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
    }

    internal sealed record MeterSource(string Identity, IReadOnlyList<VolumeSessionIdentity> Sessions, string? Error);
    internal sealed record MeterRequest(string EndpointId, IReadOnlyList<MeterSource> Sources, string? Error)
    {
        public static MeterRequest? From(AudioSessionVolumeCensus? census)
        {
            if (census is null || string.IsNullOrWhiteSpace(census.EndpointId) || census.Sources.Count == 0) return null;
            var sources = new List<MeterSource>();
            var remaining = MaximumSessions;
            foreach (var source in census.Sources.Take(MaximumSources))
            {
                if (SourceExclusions.IsExcluded(source.Sessions.FirstOrDefault()?.ExecutablePath, source.Name)) continue;
                var count = Math.Min(source.Sessions.Count, remaining);
                var sessions = Array.AsReadOnly(source.Sessions.Take(count).ToArray());
                remaining -= count;
                var error = census.Error ?? (!Same(source.EndpointId, census.EndpointId) ? "The source belongs to a different output device."
                    : count != source.Sessions.Count ? "The session meter limit was reached."
                    : count == 0 ? "No verified audio session is available."
                    : sessions.Any(session => !CanObserve(session, census.EndpointId, source.Identity))
                        ? "The owning audio session cannot be verified for per-app metering." : null);
                sources.Add(new(source.Identity, sessions, error));
            }
            if (sources.Count == 0) return null;
            return new(census.EndpointId, sources.AsReadOnly(), census.Sources.Count > MaximumSources
                || census.Sources.Sum(source => (long)source.Sessions.Count) > MaximumSessions
                    ? "Meters are limited to 256 sources and 256 sessions." : census.Error);
        }
    }

    internal static bool CanObserve(VolumeSessionIdentity identity, string endpointId, string sourceIdentity, bool allowSharedSessions = false)
    {
        if (!Same(identity.EndpointId, endpointId) || !Same(identity.SourceIdentity, sourceIdentity)
            || string.IsNullOrWhiteSpace(identity.SessionInstanceId) || (identity.IsSharedAcrossProcesses && !allowSharedSessions)) return false;
        if (identity.IsSystemSounds) return identity.SourceIdentity == "system-sounds";
        if (identity.ProcessId <= 0 || identity.ProcessStartedAtUtc is null || string.IsNullOrWhiteSpace(identity.ExecutablePath)
            || SourceExclusions.IsExcluded(identity.ExecutablePath)) return false;
        try { return Same(SourceIdentity.FromExecutablePath(identity.ExecutablePath), identity.SourceIdentity); }
        catch (ArgumentException) { return false; }
    }

    private static bool Same(string? left, string? right) => StringComparer.OrdinalIgnoreCase.Equals(left, right);
}

/// <summary>Separates lease reconciliation from high-frequency, strictly read-only sampling.</summary>
internal sealed class AudioSessionMeterSampler(IAudioSessionMeterStore store) : IDisposable
{
    private AudioSessionMeterMonitor.MeterRequest? _request;
    private IAudioSessionMeterEndpoint? _endpoint;
    private readonly Dictionary<VolumeSessionIdentity, IAudioSessionMeterSession> _meters = [];
    private IAudioSessionMeterSession?[][] _bindings = [];
    private string? _error;
    private bool _disposed;
    private bool _needsRebind;

    public void SetSources(AudioSessionMeterMonitor.MeterRequest? request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (CanReuse(request)) { _request = request; return; }
        _endpoint?.Dispose();
        _endpoint = null;
        _meters.Clear();
        _bindings = [];
        _request = request;
        _error = null;
        _needsRebind = false;
        if (request is null) return;
        _bindings = request.Sources.Select(source => new IAudioSessionMeterSession?[source.Sessions.Count]).ToArray();
        try
        {
            var identities = request.Sources.Where(source => source.Error is null).SelectMany(source => source.Sessions).Distinct().ToArray();
            if (identities.Length == 0) return;
            _endpoint = store.Open(request.EndpointId, identities);
            if (!StringComparer.OrdinalIgnoreCase.Equals(_endpoint.EndpointId, request.EndpointId))
            {
                _endpoint.Dispose();
                _endpoint = null;
                _error = "The selected output device changed; refresh the mixer.";
            }
            else
            {
                var available = _endpoint.Sessions.GroupBy(session => session.Identity.SessionInstanceId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
                foreach (var expected in identities)
                {
                    if (!available.TryGetValue(expected.SessionInstanceId, out var candidates)) continue;
                    var match = candidates.FirstOrDefault(session => AudioSessionVolumeController.Matches(session.Identity, expected));
                    if (match is not null) _meters[expected] = match;
                }
            }
        }
        catch (Exception ex) when (AudioSessionVolumeController.IsAudioFailure(ex))
        { _error = "Session peaks are temporarily unavailable: " + ex.Message; }
        // Bind once per fresh census; do not hash long executable/session identities at 20 Hz.
        // Each sample still validates the retained lease identity and reads its current peak.
        for (var source = 0; source < request.Sources.Count; source++)
            for (var session = 0; session < request.Sources[source].Sessions.Count; session++)
                _bindings[source][session] = _meters.GetValueOrDefault(request.Sources[source].Sessions[session]);
    }

    public AudioMeterFrame Sample(long generation, long sequence, long sampledAtTimestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_request is null) return new("", generation, sequence, sampledAtTimestamp, [], null);
        var values = new List<AudioSourcePeak>(_request.Sources.Count);
        // Native leases are fixed to the verified session object at reconciliation. A
        // restarted app cannot inherit a meter until a fresh census explicitly binds it.
        for (var sourceIndex = 0; sourceIndex < _request.Sources.Count; sourceIndex++)
        {
            var source = _request.Sources[sourceIndex];
            var error = source.Error ?? _error;
            float peak = 0;
            if (error is null)
            {
                for (var sessionIndex = 0; sessionIndex < source.Sessions.Count; sessionIndex++)
                {
                    var expected = source.Sessions[sessionIndex];
                    var session = _bindings[sourceIndex][sessionIndex];
                    if (session is null || !AudioSessionVolumeController.Matches(session.Identity, expected))
                    { error = "An audio session closed or changed; waiting for the next session refresh."; break; }
                    try
                    {
                        var value = session.ReadPeak();
                        if (!value.HasValue || !float.IsFinite(value.Value) || value.Value is < 0 or > 1)
                        { error = "A session peak is unavailable; waiting for the next session refresh."; break; }
                        peak = Math.Max(peak, value.Value);
                    }
                    catch (Exception ex) when (AudioSessionVolumeController.IsAudioFailure(ex))
                    { error = "A session peak is temporarily unavailable."; break; }
                }
            }
            if (error is not null) _needsRebind = true;
            values.Add(new(source.Identity, source.Sessions, error is null ? peak : null, error));
        }
        return new(_request.EndpointId, generation, sequence, sampledAtTimestamp, values.AsReadOnly(), _error ?? _request.Error ?? _endpoint?.Error);
    }

    private bool CanReuse(AudioSessionMeterMonitor.MeterRequest? request)
    {
        if (request is null || _request is null || _endpoint is null || _needsRebind
            || _error is not null || _endpoint.Error is not null || request.Error is not null || _request.Error is not null
            || !StringComparer.OrdinalIgnoreCase.Equals(request.EndpointId, _request.EndpointId)
            || request.Sources.Count != _request.Sources.Count) return false;
        for (var index = 0; index < request.Sources.Count; index++)
        {
            var next = request.Sources[index];
            var previous = _request.Sources[index];
            if (next.Error is not null || previous.Error is not null
                || !StringComparer.OrdinalIgnoreCase.Equals(next.Identity, previous.Identity)
                || !next.Sessions.SequenceEqual(previous.Sessions)) return false;
            foreach (var identity in next.Sessions)
                if (!_meters.TryGetValue(identity, out var meter)
                    || !AudioSessionVolumeController.Matches(meter.Identity, identity)) return false;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _endpoint?.Dispose(); }
        finally { _endpoint = null; _meters.Clear(); _bindings = []; store.Dispose(); }
    }
}

internal interface IAudioSessionMeterStore : IDisposable
{
    IAudioSessionMeterEndpoint Open(string endpointId, IReadOnlyList<VolumeSessionIdentity> expected);
}

internal interface IAudioSessionMeterEndpoint : IDisposable
{
    string EndpointId { get; }
    IReadOnlyList<IAudioSessionMeterSession> Sessions { get; }
    string? Error { get; }
}

internal interface IAudioSessionMeterSession
{
    VolumeSessionIdentity Identity { get; }
    float? ReadPeak();
}

internal sealed class NativeAudioSessionMeterStore(bool allowSharedSessions = false) : IAudioSessionMeterStore
{
    private readonly ProcessIdentityCache _processes = new();
    public IAudioSessionMeterEndpoint Open(string endpointId, IReadOnlyList<VolumeSessionIdentity> expected) => new NativeEndpoint(endpointId, expected, _processes, allowSharedSessions);
    public void Dispose() => _processes.Dispose();

    private sealed class NativeEndpoint : IAudioSessionMeterEndpoint
    {
        private readonly List<NativeSession> _sessions = [];
        private IMeterDeviceEnumerator? _enumerator;
        private IMeterDevice? _device;
        private IMeterSessionManager? _manager;
        private IMeterSessionEnumerator? _sessionEnumerator;
        private bool _disposed;
        public string EndpointId { get; }
        public IReadOnlyList<IAudioSessionMeterSession> Sessions => _sessions;
        public string? Error { get; private set; }

        public NativeEndpoint(string endpointId, IReadOnlyList<VolumeSessionIdentity> expected, ProcessIdentityCache processes, bool allowSharedSessions)
        {
            EndpointId = endpointId;
            try
            {
                _enumerator = (IMeterDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!)!;
                Marshal.ThrowExceptionForHR(_enumerator.GetDevice(endpointId, out _device));
                Marshal.ThrowExceptionForHR(_device.GetId(out var actual));
                EndpointId = actual;
                var managerId = typeof(IMeterSessionManager).GUID;
                Marshal.ThrowExceptionForHR(_device.Activate(ref managerId, 23, IntPtr.Zero, out var manager));
                _manager = (IMeterSessionManager)manager;
                Marshal.ThrowExceptionForHR(_manager.GetSessionEnumerator(out _sessionEnumerator));
                Marshal.ThrowExceptionForHR(_sessionEnumerator.GetCount(out var total));
                var requested = expected.GroupBy(item => item.SessionInstanceId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
                if (total > 1024) Error = "Only the first 1024 Windows sessions could be checked for metering.";
                for (var index = 0; index < Math.Min(total, 1024); index++)
                {
                    IAudioSessionControl? control = null;
                    try
                    {
                        Marshal.ThrowExceptionForHR(_sessionEnumerator.GetSession(index, out control));
                        var sessionControl = (IAudioSessionControl2)control;
                        Marshal.ThrowExceptionForHR(sessionControl.GetState(out var state));
                        if (state == AudioSessionState.AudioSessionStateExpired) continue;
                        Marshal.ThrowExceptionForHR(sessionControl.GetSessionInstanceIdentifier(out var instance));
                        if (!requested.TryGetValue(instance, out var candidates)) continue;
                        var processResult = sessionControl.GetProcessId(out var nativePid);
                        Marshal.ThrowExceptionForHR(processResult);
                        var systemResult = sessionControl.IsSystemSoundsSession();
                        Marshal.ThrowExceptionForHR(systemResult);
                        var system = systemResult == 0;
                        var pid = checked((int)nativePid);
                        var process = !system && pid > 0 ? processes.Read(pid) : null;
                        var path = process?.ExecutablePath;
                        var sourceId = system ? "system-sounds" : path is not null ? SourceIdentity.FromExecutablePath(path) : null;
                        if (sourceId is null) continue;
                        var identity = new VolumeSessionIdentity(EndpointId, sourceId, pid, process?.Identity.StartedAtUtc, path, instance, system, !system && processResult != 0);
                        if (!AudioSessionMeterMonitor.CanObserve(identity, EndpointId, sourceId, allowSharedSessions)
                            || !candidates.Any(candidate => AudioSessionVolumeController.Matches(identity, candidate))) continue;
                        _sessions.Add(new NativeSession(control, identity));
                        control = null; // One acquired COM reference, one lease release.
                    }
                    catch (Exception ex) when (AudioSessionVolumeController.IsAudioFailure(ex))
                    { Error = "Some Windows session meters are temporarily unavailable."; }
                    finally { Release(control); }
                }
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var session in _sessions) session.Dispose();
            _sessions.Clear();
            Release(_sessionEnumerator); Release(_manager); Release(_device); Release(_enumerator);
            _sessionEnumerator = null; _manager = null; _device = null; _enumerator = null;
        }
    }

    private sealed class NativeSession : IAudioSessionMeterSession, IDisposable
    {
        private readonly IAudioSessionControl _session;
        private readonly IMeterInformation _meter;
        private bool _disposed;
        public VolumeSessionIdentity Identity { get; }
        public NativeSession(IAudioSessionControl session, VolumeSessionIdentity identity)
        { _session = session; Identity = identity; _meter = (IMeterInformation)session; }
        public float? ReadPeak()
        {
            if (_disposed) return null;
            Marshal.ThrowExceptionForHR(_session.GetState(out var state));
            if (state == AudioSessionState.AudioSessionStateExpired) return null;
            Marshal.ThrowExceptionForHR(_meter.GetPeakValue(out var peak));
            return peak;
        }
        public void Dispose() { if (_disposed) return; _disposed = true; Release(_session); }
    }

    private static void Release(object? value)
    {
        try { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException) { }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMeterDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMeterDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMeterDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMeterDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMeterSessionManager
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid session, uint flags, out IAudioSessionControl control);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid session, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IMeterSessionEnumerator sessions);
    }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMeterSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl control);
    }
    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}

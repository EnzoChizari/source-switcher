using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi.Interfaces;
using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.Tray.Audio;

/// <summary>A current session instance, rather than a PID or executable alone, authorizes a volume write.</summary>
public sealed record VolumeSessionIdentity(
    string EndpointId,
    string SourceIdentity,
    int ProcessId,
    DateTimeOffset? ProcessStartedAtUtc,
    string? ExecutablePath,
    string SessionInstanceId,
    bool IsSystemSounds,
    bool IsSharedAcrossProcesses = false);

/// <summary>Mixed groups have no uniform Volume/Muted value; the UI must label them as mixed.</summary>
public sealed record AudioSessionVolumeSnapshot(
    string EndpointId,
    string Identity,
    string Name,
    float? Volume,
    bool? Muted,
    bool HasMixedVolumes,
    bool HasMixedMute,
    IReadOnlyList<VolumeSessionIdentity> Sessions,
    bool CanControl,
    bool IsActive,
    string? Error);

public sealed record AudioSessionVolumeCensus(
    string EndpointId, IReadOnlyList<AudioSessionVolumeSnapshot> Sources, string? Error);

public sealed record AudioSessionVolumeWriteResult(
    bool Success,
    int AppliedSessions,
    int MissingSessions,
    string? Error,
    AudioSessionVolumeSnapshot? Confirmed);

/// <summary>
/// Controls Windows shared-mode session gain only. No endpoint volume, format, rate-master,
/// saved setting, or future/restarted session is changed by this controller.
/// </summary>
public sealed class AudioSessionVolumeController : IDisposable
{
    private const float VolumeTolerance = 0.00001f;
    private readonly IAudioSessionVolumeStore _store;
    private readonly object _gate = new();
    private bool _disposed;

    public AudioSessionVolumeController() : this(new NativeAudioSessionVolumeStore()) { }
    internal AudioSessionVolumeController(IAudioSessionVolumeStore store) => _store = store;

    public AudioSessionVolumeCensus Read(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        lock (_gate)
        {
            if (_disposed) return new(endpointId, [], "The volume controller has closed.");
            try
            {
                using var endpoint = _store.Open(endpointId);
                if (!Same(endpoint.EndpointId, endpointId))
                    return new(endpointId, [], "The selected output device changed; refresh the mixer.");
                return ReadEndpoint(endpoint, endpointId);
            }
            catch (Exception ex) when (IsAudioFailure(ex))
            {
                return new(endpointId, [], $"Session volume is temporarily unavailable: {ex.Message}");
            }
        }
    }

    public AudioSessionVolumeWriteResult SetVolume(
        string endpointId, AudioSessionVolumeSnapshot target, float volume)
    {
        if (!float.IsFinite(volume) || volume is < 0f or > 1f)
            return new(false, 0, 0, "Volume must be between 0 and 100 percent.", null);
        return Write(endpointId, target, volume, null);
    }

    public AudioSessionVolumeWriteResult SetMute(
        string endpointId, AudioSessionVolumeSnapshot target, bool muted) => Write(endpointId, target, null, muted);

    private AudioSessionVolumeWriteResult Write(
        string endpointId, AudioSessionVolumeSnapshot target, float? volume, bool? muted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            if (_disposed) return new(false, 0, 0, "The volume controller has closed.", null);
            if (!Same(endpointId, target.EndpointId) || !target.CanControl || target.Sessions.Count == 0
                || target.Sessions.Any(session => !Same(session.EndpointId, endpointId)
                    || !Same(session.SourceIdentity, target.Identity) || !CanControl(session))
                || SourceExclusions.IsExcluded(target.Sessions[0].ExecutablePath, target.Name))
                return new(false, 0, 0, "This source or output changed; refresh the mixer before adjusting it.", null);

            var applied = 0;
            var missing = 0;
            var failed = 0;
            var verificationFailed = false;
            AudioSessionVolumeSnapshot? confirmed = null;
            try
            {
                using var endpoint = _store.Open(endpointId);
                if (!Same(endpoint.EndpointId, endpointId))
                    return new(false, 0, 0, "The selected output device changed; refresh the mixer.", null);

                // A write is authorized only for instances that were visible when the user
                // interacted. A new session of the same app must wait for a new interaction.
                var current = ReadStates(endpoint, endpointId).States;
                foreach (var expected in target.Sessions.Distinct())
                {
                    var match = current.FirstOrDefault(item => Matches(item.State.Identity, expected));
                    if (match is null || !CanControl(match.State.Identity)) { missing++; continue; }
                    try
                    {
                        // The adapter revalidates immediately before touching its session COM
                        // object, so an exit/PID reuse between census and command fails closed.
                        if (!match.Session.TrySet(expected, volume, muted, out _)) { failed++; continue; }
                        applied++;
                        var readback = match.Session.Read();
                        if (readback is null || !Matches(readback.Identity, expected)
                            || (volume.HasValue && Math.Abs(readback.Volume - volume.Value) > VolumeTolerance)
                            || (muted.HasValue && readback.Muted != muted.Value)) verificationFailed = true;
                    }
                    catch (Exception ex) when (IsAudioFailure(ex)) { failed++; }
                }

                // Fresh getters reflect another mixer changing the volume as well. A concurrent
                // external adjustment can supersede ours; never cache a requested value as truth.
                var census = ReadEndpoint(endpoint, endpointId);
                confirmed = census.Sources.FirstOrDefault(source => Same(source.Identity, target.Identity));
                if (census.Error is not null) verificationFailed = true;
            }
            catch (Exception ex) when (IsAudioFailure(ex))
            {
                return new(false, applied, missing, $"Session volume could not be confirmed: {ex.Message}", confirmed);
            }

            var success = applied > 0 && missing == 0 && failed == 0 && !verificationFailed;
            var error = missing > 0
                ? $"{missing} session(s) closed or restarted; matching sessions were updated. Refresh before adjusting the new sessions."
                : failed > 0 ? $"{failed} session adjustment(s) failed; refresh to read current volume."
                : verificationFailed ? "Windows did not confirm every requested volume change; refresh to read current volume."
                : applied == 0 ? "No matching audio session remains; refresh the mixer." : null;
            return new(success, applied, missing, error, confirmed);
        }
    }

    private static AudioSessionVolumeCensus ReadEndpoint(IAudioSessionVolumeEndpoint endpoint, string endpointId)
    {
        var read = ReadStates(endpoint, endpointId);
        var sources = read.States.GroupBy(item => item.State.Identity.SourceIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var rows = group.Select(item => item.State).ToArray();
                var volume = rows[0].Volume;
                var mute = rows[0].Muted;
                var mixedVolume = rows.Any(row => Math.Abs(row.Volume - volume) > VolumeTolerance);
                var mixedMute = rows.Any(row => row.Muted != mute);
                var canControl = rows.All(row => CanControl(row.Identity));
                return new AudioSessionVolumeSnapshot(endpointId, group.Key, rows[0].Name,
                    mixedVolume ? null : volume, mixedMute ? null : mute, mixedVolume, mixedMute,
                    rows.Select(row => row.Identity).ToArray(), canControl, rows.Any(row => row.IsActive),
                    canControl ? null : rows.Any(row => row.Identity.IsSharedAcrossProcesses)
                        ? "Windows reports a session shared by multiple processes; per-app control is read only."
                        : "The owning process cannot be verified; volume is read only.");
            })
            .OrderByDescending(source => source.IsActive)
            .ThenBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(endpointId, sources, read.Failed ? "Some sessions changed while reading; refresh to retry." : endpoint.Error);
    }

    private static (List<SessionState> States, bool Failed) ReadStates(IAudioSessionVolumeEndpoint endpoint, string endpointId)
    {
        var rows = new List<SessionState>();
        var failed = false;
        foreach (var session in endpoint.Sessions)
        {
            try
            {
                var state = session.Read();
                if (state is null) continue;
                if (!Same(state.Identity.EndpointId, endpointId) || !float.IsFinite(state.Volume)
                    || state.Volume is < 0 or > 1 || string.IsNullOrWhiteSpace(state.Identity.SessionInstanceId))
                { failed = true; continue; }
                if (SourceExclusions.IsExcluded(state.Identity.ExecutablePath, state.Name)) continue;
                rows.Add(new(session, state));
            }
            catch (Exception ex) when (IsAudioFailure(ex)) { failed = true; }
        }
        return (rows, failed);
    }

    internal static bool Matches(VolumeSessionIdentity left, VolumeSessionIdentity right) =>
        Same(left.EndpointId, right.EndpointId) && Same(left.SourceIdentity, right.SourceIdentity)
        && left.ProcessId == right.ProcessId && left.ProcessStartedAtUtc == right.ProcessStartedAtUtc
        && Same(left.ExecutablePath, right.ExecutablePath) && left.IsSystemSounds == right.IsSystemSounds
        && left.IsSharedAcrossProcesses == right.IsSharedAcrossProcesses
        && StringComparer.Ordinal.Equals(left.SessionInstanceId, right.SessionInstanceId);

    private static bool CanControl(VolumeSessionIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.SessionInstanceId)) return false;
        if (identity.IsSystemSounds) return identity.SourceIdentity == "system-sounds";
        if (identity.IsSharedAcrossProcesses) return false;
        if (identity.ProcessId <= 0 || identity.ProcessStartedAtUtc is null || identity.ExecutablePath is null
            || SourceExclusions.IsExcluded(identity.ExecutablePath)) return false;
        try { return Same(SourceIdentity.FromExecutablePath(identity.ExecutablePath), identity.SourceIdentity); }
        catch (ArgumentException) { return false; }
    }

    private static bool Same(string? left, string? right) => StringComparer.OrdinalIgnoreCase.Equals(left, right);
    internal static bool IsAudioFailure(Exception ex) => ex is COMException or InvalidOperationException
        or UnauthorizedAccessException or ArgumentException or InvalidCastException or System.ComponentModel.Win32Exception;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _store.Dispose();
        }
    }

    private sealed record SessionState(IAudioSessionVolumeSession Session, AudioVolumeSessionState State);
}

internal sealed record AudioVolumeSessionState(VolumeSessionIdentity Identity, string Name, float Volume, bool Muted, bool IsActive);
internal sealed record AudioSessionIdentityState(VolumeSessionIdentity Identity, string Name, bool IsActive);

internal interface IAudioSessionVolumeStore : IDisposable
{
    IAudioSessionVolumeEndpoint Open(string endpointId);
}

internal interface IAudioSessionVolumeEndpoint : IDisposable
{
    string EndpointId { get; }
    IReadOnlyList<IAudioSessionVolumeSession> Sessions { get; }
    string? Error { get; }
}

internal interface IAudioSessionVolumeSession
{
    AudioVolumeSessionState? Read();
    AudioSessionIdentityState? ReadIdentity()
    {
        var state = Read();
        return state is null ? null : new(state.Identity, state.Name, state.IsActive);
    }
    bool TrySet(VolumeSessionIdentity expected, float? volume, bool? muted, out string? error);
}

internal sealed class NativeAudioSessionVolumeStore : IAudioSessionVolumeStore
{
    private readonly ProcessIdentityCache _processes = new();
    public IAudioSessionVolumeEndpoint Open(string endpointId) => new NativeEndpoint(endpointId, _processes);
    public void Dispose() => _processes.Dispose();

    private sealed class NativeEndpoint : IAudioSessionVolumeEndpoint
    {
        private readonly List<NativeSession> _sessions = [];
        private IVolumeDeviceEnumerator? _enumerator;
        private IVolumeDevice? _device;
        private IVolumeSessionManager? _manager;
        private IVolumeSessionEnumerator? _sessionEnumerator;
        private bool _disposed;
        public string EndpointId { get; }
        public IReadOnlyList<IAudioSessionVolumeSession> Sessions => _sessions;
        public string? Error { get; private set; }

        public NativeEndpoint(string endpointId, ProcessIdentityCache processes)
        {
            EndpointId = endpointId;
            try
            {
                _enumerator = (IVolumeDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(
                    new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), throwOnError: true)!)!;
                Marshal.ThrowExceptionForHR(_enumerator.GetDevice(endpointId, out _device));
                Marshal.ThrowExceptionForHR(_device.GetId(out var actualId));
                EndpointId = actualId;
                var managerId = typeof(IVolumeSessionManager).GUID;
                Marshal.ThrowExceptionForHR(_device.Activate(ref managerId, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var manager));
                _manager = (IVolumeSessionManager)manager;
                Marshal.ThrowExceptionForHR(_manager.GetSessionEnumerator(out _sessionEnumerator));
                Marshal.ThrowExceptionForHR(_sessionEnumerator.GetCount(out var total));
                // Bound a pathological application creating thousands of sessions. Report a
                // partial census instead of silently implying this is the entire device.
                var count = Math.Min(total, 1024);
                if (count != total) Error = "Only the first 1024 audio sessions are shown.";
                for (var index = 0; index < count; index++)
                {
                    IAudioSessionControl? control = null;
                    try
                    {
                        Marshal.ThrowExceptionForHR(_sessionEnumerator.GetSession(index, out control));
                        _sessions.Add(new NativeSession(control, EndpointId, processes));
                        control = null; // Ownership transferred to the lease.
                    }
                    catch (Exception ex) when (AudioSessionVolumeController.IsAudioFailure(ex))
                    { Error = "Some audio sessions changed while opening the mixer; refresh to retry."; }
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
            Release(_sessionEnumerator);
            Release(_manager);
            Release(_device);
            Release(_enumerator);
            _sessionEnumerator = null;
            _manager = null;
            _device = null;
            _enumerator = null;
        }
    }

    private sealed class NativeSession(IAudioSessionControl session, string endpointId, ProcessIdentityCache processes)
        : IAudioSessionVolumeSession, IDisposable
    {
        private readonly IAudioSessionControl2 _control = (IAudioSessionControl2)session;
        private IVolumeSimpleAudioVolume? _volume;
        private IVolumeSimpleAudioVolume Volume => _volume ??= (IVolumeSimpleAudioVolume)session;
        private bool _disposed;

        public AudioVolumeSessionState? Read()
        {
            var identity = ReadIdentity();
            if (identity is null) return null;
            Marshal.ThrowExceptionForHR(Volume.GetMasterVolume(out var volume));
            Marshal.ThrowExceptionForHR(Volume.GetMute(out var muted));
            return new(identity.Identity, identity.Name, volume, muted, identity.IsActive);
        }

        public AudioSessionIdentityState? ReadIdentity()
        {
            if (_disposed) return null;
            Marshal.ThrowExceptionForHR(_control.GetState(out var state));
            if (state == AudioSessionState.AudioSessionStateExpired) return null;
            Marshal.ThrowExceptionForHR(_control.GetSessionInstanceIdentifier(out var instance));
            if (string.IsNullOrWhiteSpace(instance)) return null;
            var processResult = _control.GetProcessId(out var nativePid);
            Marshal.ThrowExceptionForHR(processResult);
            var pid = checked((int)nativePid);
            var systemResult = _control.IsSystemSoundsSession();
            Marshal.ThrowExceptionForHR(systemResult);
            var system = systemResult == 0;
            // AUDCLNT_S_NO_SINGLE_PROCESS is a success HRESULT, but its PID identifies
            // only the initial creator. Treat every non-S_OK success as shared/readonly.
            var shared = !system && processResult != 0;
            var process = !system && pid > 0 ? processes.Read(pid) : null;
            var path = process?.ExecutablePath;
            var name = system ? "System sounds" : path is not null ? Path.GetFileNameWithoutExtension(path) : ProcessName(pid);
            if (SourceExclusions.IsExcluded(path, name)) return null;
            var identity = system ? "system-sounds" : path is not null ? SourceIdentity.FromExecutablePath(path)
                : $"unavailable:{endpointId}:{pid}:{instance}";
            return new(new(endpointId, identity, pid, process?.Identity.StartedAtUtc, path, instance, system, shared),
                shared ? name + " (shared session)" : name, state == AudioSessionState.AudioSessionStateActive);
        }

        public bool TrySet(VolumeSessionIdentity expected, float? volume, bool? muted, out string? error)
        {
            var state = Read();
            if (state is null || !AudioSessionVolumeController.Matches(state.Identity, expected))
            { error = "The audio session changed before its volume could be adjusted."; return false; }
            var context = Guid.Empty;
            if (volume.HasValue) Marshal.ThrowExceptionForHR(Volume.SetMasterVolume(volume.Value, ref context));
            if (muted.HasValue) Marshal.ThrowExceptionForHR(Volume.SetMute(muted.Value, ref context));
            error = null;
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Release(session);
        }

        private static string ProcessName(int processId)
        {
            try { using var process = Process.GetProcessById(processId); return process.ProcessName; }
            catch (Exception ex) when (AudioSessionVolumeController.IsAudioFailure(ex)) { return "Unavailable process"; }
        }
    }

    private static void Release(object? target)
    {
        // Exactly one release per COM out parameter/activation acquired by this backend.
        // Never FinalRelease a shared RCW that another part of the application may use.
        if (target is not null && Marshal.IsComObject(target)) Marshal.ReleaseComObject(target);
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVolumeDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IVolumeDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IVolumeDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVolumeDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVolumeSessionManager
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid session, uint flags, out IAudioSessionControl control);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid session, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IVolumeSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVolumeSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl control);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVolumeSimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float volume, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float volume);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
}

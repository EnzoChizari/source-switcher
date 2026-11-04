using System.Security.Cryptography;
using System.Text;
using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.Tray.Audio;

/// <summary>
/// Reads session ownership and activity only. Windows session APIs do not disclose the native
/// sample rate of another application's content or its IAudioClient stream format.
/// </summary>
public sealed class AudioSessionEnumerator : IDisposable
{
    private readonly IAudioSessionVolumeStore _store;
    private readonly object _gate = new();
    private bool _disposed;
    public string? LastError { get; private set; }

    public AudioSessionEnumerator() : this(new NativeAudioSessionVolumeStore()) { }
    internal AudioSessionEnumerator(IAudioSessionVolumeStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    public IReadOnlyList<PlatformAudioSource> Enumerate(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        lock (_gate)
        {
            LastError = null;
            if (_disposed) { LastError = "Audio session enumeration has closed."; return []; }
            var rows = new List<SessionRow>();
            try
            {
                // Every acquired endpoint/session COM reference is released on this worker.
                // Identity reads never query or adjust volume and do not activate an audio client.
                using var endpoint = _store.Open(endpointId);
                if (!StringComparer.OrdinalIgnoreCase.Equals(endpoint.EndpointId, endpointId))
                { LastError = "The selected output changed while reading audio sessions."; return []; }
                foreach (var session in endpoint.Sessions)
                {
                    try
                    {
                        var state = session.ReadIdentity();
                        if (state is null) continue;
                        if (!StringComparer.OrdinalIgnoreCase.Equals(state.Identity.EndpointId, endpointId)
                            || string.IsNullOrWhiteSpace(state.Identity.SessionInstanceId))
                        { LastError = "An audio session changed while it was being read; the next refresh will retry."; continue; }
                        if (ReadSession(state, endpointId) is { } row) rows.Add(row);
                    }
                    catch (Exception ex) when (AudioSessionVolumeController.IsAudioFailure(ex))
                    { LastError = "An audio session changed while it was being read; the next refresh will retry."; }
                }
                LastError ??= endpoint.Error;
            }
            catch (Exception ex) when (AudioSessionVolumeController.IsAudioFailure(ex))
            { LastError = $"Audio sessions are temporarily unavailable: {ex.Message}"; }
            return GroupRows(endpointId, rows);
        }
    }

    internal static IReadOnlyList<PlatformAudioSource> GroupRows(string endpointId, IReadOnlyList<SessionRow> rows) =>
        rows.GroupBy(row => row.Path is { } path
                ? SourceIdentity.FromExecutablePath(AppleMusicIdentity.CanonicalPath(path) ?? path) : row.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new PlatformAudioSource(
                group.Key,
                AppleMusicIdentity.IsOfficialExecutable(group.First().Path) ? "Apple Music" : group.First().Name,
                group.First().Path is { } path ? AppleMusicIdentity.CanonicalPath(path) ?? path : null,
                group.Select(row => row.ProcessId).Where(pid => pid > 0).Distinct().Order().ToArray(),
                group.Any(row => row.IsActive),
                group.All(row => row.Path is not null),
                ComputeToken(endpointId, group.Select(row => row.InstanceToken)))
                { SessionInstanceIds = group.Select(row => row.SessionInstanceId).Distinct(StringComparer.Ordinal).ToArray(),
                  ProcessExecutablePaths = group.Where(row => row.ProcessId > 0 && row.Path is not null)
                    .GroupBy(row => row.ProcessId).ToDictionary(rows => rows.Key, rows => rows.First().Path!) })
            .OrderByDescending(source => source.IsActive)
            .ThenBy(source => source.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static SessionRow? ReadSession(AudioSessionIdentityState session, string endpointId)
    {
        var pid = session.Identity.ProcessId;
        var instance = session.Identity.SessionInstanceId;
        var active = session.IsActive;
        if (session.Identity.IsSystemSounds)
            return new("system-sounds", "System sounds", null, pid, active, instance, instance);

        var path = session.Identity.ExecutablePath;
        if (SourceExclusions.IsExcluded(path, session.Name))
            return null;
        if (path is not null)
        {
            try
            {
                _ = SourceIdentity.FromExecutablePath(path);
                path = Path.GetFullPath(path);
                var canonical = AppleMusicIdentity.CanonicalPath(path) ?? path;
                return new(SourceIdentity.FromExecutablePath(canonical), Path.GetFileNameWithoutExtension(canonical),
                    path, pid, active, $"{pid}:{instance}", instance);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                path = null;
            }
        }

        // Show access-denied and exited owners, but do not permit selection/persistence by PID.
        // PIDs can be reused by an unrelated app; the instance token keeps their rows separate.
        return new($"unavailable:{endpointId}:{pid}:{instance}", $"{session.Name} (path unavailable)", null,
            pid, active, $"{pid}:{instance}", instance);
    }

    private static string ComputeToken(string endpointId, IEnumerable<string> instances)
    {
        var text = endpointId + "\n" + string.Join("\n", instances.Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _store.Dispose();
        }
    }

    internal sealed record SessionRow(string Key, string Name, string? Path, int ProcessId, bool IsActive,
        string InstanceToken, string SessionInstanceId);
}

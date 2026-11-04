using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.Tray.Audio;

internal sealed record AppleRenderObservation(bool? Active, float? Peak);

/// <summary>Transaction-scoped metering of current Apple-associated session identities.
/// No endpoint master meter, guessed PID, orphan session, audio capture or transport writes.</summary>
internal sealed class AppleMusicRenderProbe : IDisposable
{
    private readonly AudioSessionVolumeController census = new();
    private readonly NativeAudioSessionMeterStore meters = new(allowSharedSessions: true);

    internal static IReadOnlyList<VolumeSessionIdentity> SelectSessions(AudioSessionVolumeCensus current, string sourceKey) =>
        current.Sources.Where(source => source.IsActive)
            .SelectMany(source => source.Sessions)
            .Where(identity => !identity.IsSystemSounds
                && identity.ProcessId > 0 && identity.ProcessStartedAtUtc is not null
                && identity.ExecutablePath is { } path && AppleMusicIdentity.IsOfficialExecutable(path)
                && StringComparer.OrdinalIgnoreCase.Equals(
                    SourceIdentity.FromExecutablePath(AppleMusicIdentity.CanonicalPath(path) ?? path), sourceKey))
            .Distinct().ToArray();

    public AppleRenderObservation Read(string endpointId, string sourceKey)
    {
        try
        {
            var current = census.Read(endpointId);
            var expected = SelectSessions(current, sourceKey);
            if (expected.Count == 0) return new(current.Error is null ? false : null, null);
            using var lease = meters.Open(endpointId, expected);
            var values = lease.Sessions.Select(session => session.ReadPeak())
                .Where(peak => peak is { } value && float.IsFinite(value) && value is >= 0 and <= 1).ToArray();
            return new(true, values.Length == 0 ? null : values.Max());
        }
        catch (Exception error) when (AudioSessionVolumeController.IsAudioFailure(error))
        { return new(null, null); }
    }
    public void Dispose() { meters.Dispose(); census.Dispose(); }
}

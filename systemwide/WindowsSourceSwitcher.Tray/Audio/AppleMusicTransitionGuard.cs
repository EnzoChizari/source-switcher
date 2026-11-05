using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.Tray.Audio;

/// <summary>Keeps a playing Apple master through a brief WASAPI restart between tracks.</summary>
internal sealed class AppleMusicTransitionGuard
{
    internal static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(1);
    private string? _source;
    private TimeSpan? _started;

    internal TimeSpan? RetryAfter(PlatformAudioSource? currentMaster, FavoritePlaybackState playback, TimeSpan now)
    {
        if (currentMaster?.SupportsAutomaticRate != true || currentMaster.IsActive
            || playback != FavoritePlaybackState.Playing)
        {
            _source = null;
            _started = null;
            return null;
        }
        if (!StringComparer.OrdinalIgnoreCase.Equals(_source, currentMaster.Identity))
        {
            _source = currentMaster.Identity;
            _started = now;
        }
        var remaining = MaximumWait - (now - _started!.Value);
        return remaining > TimeSpan.Zero ? remaining : null;
    }
}

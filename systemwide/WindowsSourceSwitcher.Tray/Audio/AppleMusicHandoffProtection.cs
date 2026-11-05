using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.Tray.Audio;

/// <summary>Keeps short gaps in Apple Music's session evidence from exposing a playing stream to an automatic handoff.</summary>
internal sealed class AppleMusicHandoffProtection
{
    private static readonly TimeSpan MissingEvidenceGrace = TimeSpan.FromSeconds(2);
    private string? _endpoint;
    private TimeSpan? _lastPlaying;

    internal bool Observe(string? endpoint, bool appleOnOutput, FavoritePlaybackState playback, TimeSpan now)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(_endpoint, endpoint) || playback == FavoritePlaybackState.NotPlaying)
            _lastPlaying = null;
        _endpoint = endpoint;
        if (endpoint is null) return false;
        if (appleOnOutput && playback == FavoritePlaybackState.Playing) _lastPlaying = now;
        return _lastPlaying is { } playing && now - playing < MissingEvidenceGrace;
    }
}

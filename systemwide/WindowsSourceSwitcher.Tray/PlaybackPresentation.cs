using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;

namespace WindowsSourceSwitcher.Tray;

internal static class PlaybackPresentation
{
    // Media status is an additional control signal; retain the original Windows session
    // snapshot for stream association, diagnostics and native session ownership.
    public static SourceSnapshot ForRatePolicy(PlatformAudioSource source,
        IReadOnlyDictionary<string, FavoritePlaybackState> playback) => source.ToSnapshot() with
        { IsActive = source.IsActive && playback.GetValueOrDefault(source.Identity) != FavoritePlaybackState.NotPlaying };

}

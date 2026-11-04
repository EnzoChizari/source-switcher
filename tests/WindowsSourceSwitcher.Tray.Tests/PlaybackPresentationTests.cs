using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class PlaybackPresentationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Paused_Apple_track_metadata_is_visible_without_changing_the_held_output(bool windowsActive)
    {
        const string path = @"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.1540.23042.0_x64__nzyj5cx40ttqa\AppleMusic.exe";
        var source = new PlatformAudioSource(SourceIdentity.FromExecutablePath(path), "Apple Music", path,
            [20], windowsActive, true, "apple-session");
        var settings = new SourceSettings { SwitchingEnabled = true, CatalogRateDetectionEnabled = true };
        settings.SelectMaster(source.Identity, source.ProcessName);
        settings.LastKnownSourceKey = source.Identity;
        settings.LastKnownSampleRateHz = 44100;
        var track = new RateObservation(source.Identity, source.SessionToken, settings.SelectionRevision,
            96000, RateProvenance.CatalogMetadata, "Current track metadata");

        var display = SourceRatePresentation.Describe(source, settings, track, playback: FavoritePlaybackState.NotPlaying);
        var decision = SampleRatePolicy.Evaluate(settings,
            [PlaybackPresentation.ForRatePolicy(source, Status(source, FavoritePlaybackState.NotPlaying))], [track]);

        Assert.Equal("96 kHz est.", display.Text);
        Assert.Equal(RateDecisionReason.SourcePaused, decision.Reason);
        Assert.Equal(44100, decision.DesiredSampleRateHz);
    }

    [Fact]
    public void Confirmed_media_pause_is_visible_before_Windows_session_becomes_idle()
    {
        var source = Source("Spotify");
        var settings = new SourceSettings();
        settings.SelectMaster(source.Identity, source.ProcessName);
        settings.LastKnownSourceKey = source.Identity;
        settings.LastKnownSampleRateHz = 96000;
        var playback = Status(source, FavoritePlaybackState.NotPlaying);

        var row = Assert.Single(SourceListPresentation.Build([source], settings, playback));
        Assert.Equal("Paused", row.SessionText);
        Assert.Equal("96 kHz held", SourceRatePresentation.Describe(source, settings, playback: FavoritePlaybackState.NotPlaying).Text);
        Assert.False(PlaybackPresentation.ForRatePolicy(source, playback).IsActive);
        Assert.True(source.IsActive); // Native session evidence is never rewritten.
        Assert.Same(source, row.LiveSource);
    }

    [Theory]
    [InlineData(FavoritePlaybackState.Unknown, "Active")]
    [InlineData(FavoritePlaybackState.Playing, "Playing")]
    [InlineData(FavoritePlaybackState.NotPlaying, "Paused")]
    public void Source_rows_distinguish_confirmed_media_state_from_raw_Windows_activity(FavoritePlaybackState state, string text)
    {
        var source = Source("Spotify");
        Assert.Equal(text, Assert.Single(SourceListPresentation.Build([source], new(), Status(source, state))).SessionText);
    }

    [Theory]
    [InlineData(FavoritePlaybackState.Unknown)]
    [InlineData(FavoritePlaybackState.Playing)]
    [InlineData(FavoritePlaybackState.NotPlaying)]
    public void Inactive_Windows_session_cannot_be_a_playing_auto_candidate(FavoritePlaybackState state)
    {
        var source = Source("Spotify") with { IsActive = false };
        var result = new FavoritePlaybackSnapshot(1, [new("Spotify.exe", state)]).ResolveForFavorites([source]);
        Assert.Equal(FavoritePlaybackState.NotPlaying, result[source.Identity]);
        Assert.Equal("Idle / paused", Assert.Single(SourceListPresentation.Build([source], new(), Status(source, state))).SessionText);
    }

    [Fact]
    public void Spotify_and_Apple_Music_require_media_evidence_for_automatic_favorite_selection()
    {
        var spotify = Source("Spotify");
        const string path = @"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.1540.23042.0_x64__nzyj5cx40ttqa\AppleMusic.exe";
        var apple = new PlatformAudioSource(SourceIdentity.FromExecutablePath(path), "Apple Music", path, [20], true, true, "apple");
        var result = new FavoritePlaybackSnapshot(1, []).ResolveForFavorites([spotify, apple]);
        Assert.Equal(FavoritePlaybackState.Unknown, result[spotify.Identity]);
        Assert.Equal(FavoritePlaybackState.Unknown, result[apple.Identity]);
    }

    [Fact]
    public void Generic_apps_can_use_Windows_activity_without_inventing_media_status()
    {
        var source = Source("Game");
        Assert.Equal(FavoritePlaybackState.Playing, new FavoritePlaybackSnapshot(1, []).ResolveForFavorites([source])[source.Identity]);
        Assert.Equal("Active", Assert.Single(SourceListPresentation.Build([source], new())).SessionText);
    }

    [Fact]
    public void Confirmed_generic_pause_overrides_Windows_activity()
    {
        var source = Source("Player");
        Assert.Equal(FavoritePlaybackState.NotPlaying, new FavoritePlaybackSnapshot(1, [new("Player.exe", FavoritePlaybackState.NotPlaying)]).ResolveForFavorites([source])[source.Identity]);
    }

    [Fact]
    public void Manual_rate_remains_available_during_media_pause()
    {
        var source = Source("Spotify");
        var settings = new SourceSettings();
        settings.ManualSampleRatesHz[source.Identity] = 192000;
        Assert.Equal("192 kHz manual", SourceRatePresentation.Describe(source, settings, playback: FavoritePlaybackState.NotPlaying).Text);
    }

    private static PlatformAudioSource Source(string name)
    {
        var path = @"C:\Apps\" + name + ".exe";
        return new(SourceIdentity.FromExecutablePath(path), name, path, [10], true, true, "session");
    }
    private static Dictionary<string, FavoritePlaybackState> Status(PlatformAudioSource source, FavoritePlaybackState state)
        => new(StringComparer.OrdinalIgnoreCase) { [source.Identity] = state };
}

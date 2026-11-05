using WindowsLosslessSwitcher.Models;
using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class AppleMusicQualityTests
{
    [Theory]
    [InlineData(RateDecisionReason.SourcePaused)]
    [InlineData(RateDecisionReason.NativeRateUnknown)]
    public void AppleManualDepthKeepsCurrentRateWhenTrackRateIsUnavailable(RateDecisionReason reason)
    {
        const string apple = @"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.1540.23042.0_x64__nzyj5cx40ttqa\AppleMusic.exe";
        SourceSettings settings = new() { FollowSourceBitDepth = true };
        settings.SelectMaster(apple, "Apple Music");
        settings.ManualBitDepths[apple] = 32;
        RateDecision held = new(192000, reason, "Last confirmed rate");
        var applied = AppleMusicFormatPolicy.ManualDepthAtCurrentRate(settings, held, 48000);
        Assert.Equal(48000, applied?.DesiredSampleRateHz);
        Assert.Equal(32, applied?.DesiredBitsPerSample);
        Assert.True(applied?.ManualBitDepth);
        Assert.Null(AppleMusicFormatPolicy.ManualDepthAtCurrentRate(settings, held with { Reason = RateDecisionReason.PlayerMetadata }, 48000));
        Assert.Null(AppleMusicFormatPolicy.ManualDepthAtCurrentRate(settings, held with { Reason = RateDecisionReason.SourceUnavailable }, 48000));
        settings.FollowSourceBitDepth = false;
        Assert.Null(AppleMusicFormatPolicy.ManualDepthAtCurrentRate(settings, held, 48000));
    }

    [Theory]
    [InlineData("Spotify.exe")]
    [InlineData("Discord.exe")]
    public void OtherPlayersDoNotUseAppleManualDepthRecovery(string executable)
    {
        var path = @"C:\Apps\" + executable;
        SourceSettings settings = new() { FollowSourceBitDepth = true };
        settings.SelectMaster(path, executable);
        settings.ManualBitDepths[path] = 32;
        Assert.Null(AppleMusicFormatPolicy.ManualDepthAtCurrentRate(settings,
            new(null, RateDecisionReason.SourcePaused, ""), 48000));
    }

    [Theory]
    [InlineData("24-bit 44.1 kHz ALAC", 44100, 24)]
    [InlineData("24‑bit 88,2 kHz ALAC", 88200, 24)]
    [InlineData("24-bit 192 kHz ALAC", 192000, 24)]
    [InlineData("16-bit 48 kHz ALAC", 48000, 16)]
    [InlineData("44.1 kHz ALAC", 44100, null)]
    public void ReadsExplicitLosslessFormats(string label, int rate, int? bits) =>
        Assert.Equal(new AppleMusicQualityReader.Format(rate, bits), AppleMusicQualityReader.Parse(label));

    [Theory]
    [InlineData("Lossless")]
    [InlineData("24-bit 44.1 kHz AAC")]
    [InlineData("24-bit 44.1001 kHz ALAC")]
    [InlineData("24-bit 9999999999999999999 kHz ALAC")]
    [InlineData("24-bit 0.0441 kHz ALAC")]
    public void RejectsAmbiguousAndInvalidFormats(string label) =>
        Assert.Null(AppleMusicQualityReader.Parse(label));

    [Fact]
    public void MatchesTheAlbumAsWellAsTitleAndArtist()
    {
        TrackSnapshot track = new("Apple", "1", "Track", "Artist", "Album", "Test", DateTimeOffset.UnixEpoch);
        Assert.True(AppleMusicQualityReader.CaptionMatches("Track Artist — Album", track));
        Assert.True(AppleMusicQualityReader.CaptionMatches("Track  Artist — Album Explicit", track));
        Assert.False(AppleMusicQualityReader.CaptionMatches("Track Artist — Single", track));
        Assert.False(AppleMusicQualityReader.CaptionMatches("Track Artist — Album", track with { Album = null }));
    }

    [Fact]
    public void PlayerReportBeatsCatalogAndSubmittedPcmAndTargetsTheSupportedDepth()
    {
        const string path = @"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.1540.23042.0_x64__nzyj5cx40ttqa\AppleMusic.exe";
        PlatformAudioSource apple = new(path, "Apple Music", path, [1], true, true, "session");
        SourceSettings settings = new() { CatalogRateDetectionEnabled = true, StreamRateDetectionEnabled = true, FollowSourceBitDepth = true };
        settings.SelectMaster(apple.Identity, apple.ProcessName);
        var report = new AppleMusicQualityReader.Format(44100, 24).Observe(apple, settings.SelectionRevision);
        var catalog = report with { Provenance = RateProvenance.CatalogMetadata, SampleRateHz = 192000 };
        var stream = report with { Provenance = RateProvenance.WasapiStream, SampleRateHz = 48000, BitsPerSample = 16 };
        var decision = SampleRatePolicy.Evaluate(settings, [apple.ToSnapshot()], [stream, catalog, report]);

        Assert.Equal(RateDecisionReason.PlayerMetadata, decision.Reason);
        Assert.Equal(44100, decision.DesiredSampleRateHz);
        Assert.Equal(24, decision.DesiredBitsPerSample);
        Assert.Equal("44.1 kHz / 24-bit track", SourceRatePresentation.Describe(apple, settings, report, stream).Text);
        Assert.Equal(new EndpointFormat(44100, 24, 2), EndpointFormatPolicy.SelectExact(new(48000, 16, 2),
            [new(44100, 16, 2), new(44100, 24, 2)], Assert.IsType<int>(decision.DesiredSampleRateHz), decision.DesiredBitsPerSample,
            preventDepthReduction: true));
        Assert.Equal(RateDecisionReason.CatalogMetadata,
            SampleRatePolicy.Evaluate(settings, [apple.ToSnapshot()], [stream, catalog, report with { SessionToken = "old" }]).Reason);
        var rateOnly = SampleRatePolicy.Evaluate(settings, [apple.ToSnapshot()], [stream, catalog, report with { BitsPerSample = null }]);
        Assert.Equal(44100, rateOnly.DesiredSampleRateHz);
        Assert.Null(rateOnly.DesiredBitsPerSample);
    }

    [Fact]
    public void HoldsForAnAutomaticHandoffWhileApplePlaysAndReleasesOnPause()
    {
        AppleMusicHandoffProtection protection = new();
        var playback = new FavoritePlaybackSnapshot(1,
            [new(FavoritePlaybackSnapshot.AppleMusicAppId, FavoritePlaybackState.Playing)]);
        Assert.True(protection.Observe("DAC", true, playback.AppleMusicPlayback, TimeSpan.Zero));
        Assert.True(protection.Observe("DAC", false, playback.AppleMusicPlayback, TimeSpan.FromMilliseconds(500)));
        Assert.True(protection.Observe("DAC", true, playback.AppleMusicPlayback, TimeSpan.FromSeconds(10)));
        Assert.False(protection.Observe("DAC", true, FavoritePlaybackState.NotPlaying, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void MissingAppleSessionExpiresAndChangingOutputClearsProtection()
    {
        AppleMusicHandoffProtection protection = new();
        Assert.True(protection.Observe("DAC", true, FavoritePlaybackState.Playing, TimeSpan.Zero));
        Assert.True(protection.Observe("dac", false, FavoritePlaybackState.Unknown, TimeSpan.FromMilliseconds(1999)));
        Assert.False(protection.Observe("DAC", false, FavoritePlaybackState.Unknown, TimeSpan.FromSeconds(2)));
        Assert.True(protection.Observe("DAC", true, FavoritePlaybackState.Playing, TimeSpan.FromSeconds(3)));
        Assert.False(protection.Observe("Headphones", false, FavoritePlaybackState.Playing, TimeSpan.FromSeconds(3)));
    }
}

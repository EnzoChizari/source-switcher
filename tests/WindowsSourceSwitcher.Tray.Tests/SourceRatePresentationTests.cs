using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class SourceRatePresentationTests
{
    [Fact]
    public void GenericAppsExplainMissingDetectorEvenWhenCatalogPreferenceIsEnabled()
    {
        var source = GenericSource();
        var display = SourceRatePresentation.Describe(source, Settings(source), Observation(source));

        Assert.Equal("No detector", display.Text);
        Assert.Contains("not exposed by Windows audio sessions", display.Detail);
        Assert.Contains("manual rate", display.Detail);
        Assert.Null(display.Evidence);
    }

    [Fact]
    public void ManualTargetIsClearlyLabeledAndTakesPriorityOverAnEstimate()
    {
        var source = AppleSource();
        var settings = Settings(source);
        settings.ManualSampleRatesHz[source.Identity] = 48000;

        var display = SourceRatePresentation.Describe(source, settings, Observation(source));

        Assert.Equal("48 kHz manual", display.Text);
        Assert.Contains("not a detected native/content rate", display.Detail);
        Assert.Null(display.Evidence);
    }

    [Fact]
    public void ManualTargetRemainsVisibleWhileAnAppIsPaused()
    {
        var source = GenericSource() with { IsActive = false };
        var settings = Settings(source);
        settings.ManualSampleRatesHz[source.Identity.ToLowerInvariant()] = 44100;
        settings.LastKnownSourceKey = source.Identity;
        settings.LastKnownSampleRateHz = 96000;

        Assert.Equal("44.1 kHz manual", SourceRatePresentation.Describe(source, settings).Text);
    }

    [Fact]
    public void CurrentCatalogObservationDisplaysEstimateAndPreservesEvidence()
    {
        var source = AppleSource();
        var observation = Observation(source);

        var display = SourceRatePresentation.Describe(source, Settings(source), observation);

        Assert.Equal("44.1 kHz est.", display.Text);
        Assert.Contains("delivered asset remains unverified", display.Detail);
        Assert.Equal(observation.Detail, display.Evidence);
    }

    [Theory]
    [InlineData("different-source")]
    [InlineData("different-session")]
    [InlineData("old-selection")]
    [InlineData("missing-rate")]
    [InlineData("rate-too-low")]
    [InlineData("rate-too-high")]
    public void StaleOrInvalidObservationCannotBeDisplayedAsAnEstimate(string mismatch)
    {
        var source = AppleSource();
        var observation = Observation(source);
        observation = mismatch switch
        {
            "different-source" => observation with { SourceKey = "OTHER" },
            "different-session" => observation with { SessionToken = "previous-session" },
            "old-selection" => observation with { SelectionRevision = 1 },
            "missing-rate" => observation with { SampleRateHz = null },
            "rate-too-low" => observation with { SampleRateHz = 7999 },
            "rate-too-high" => observation with { SampleRateHz = 768001 },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };

        var display = SourceRatePresentation.Describe(source, Settings(source), observation);

        Assert.Equal("Estimate pending", display.Text);
        Assert.Null(display.Evidence);
    }

    [Theory]
    [InlineData(RateProvenance.Unknown)]
    [InlineData(RateProvenance.WindowsMixFormat)]
    [InlineData(RateProvenance.ApplicationReported)]
    [InlineData(RateProvenance.VerifiedNative)]
    public void NonCatalogProvenanceCannotMasqueradeAsTheImplementedCatalogDetector(RateProvenance provenance)
    {
        var source = AppleSource();
        var observation = Observation(source) with { Provenance = provenance };
        var display = SourceRatePresentation.Describe(source, Settings(source), observation);

        Assert.Equal("Estimate pending", display.Text);
        Assert.Null(display.Evidence);
    }

    [Fact]
    public void PausedSourceShowsCurrentTrackEstimateIndependentlyOfHeldOutput()
    {
        var source = AppleSource() with { IsActive = false };
        var settings = Settings(source);
        settings.LastKnownSourceKey = source.Identity;
        settings.LastKnownSampleRateHz = 44100;
        var observation = Observation(source) with { SampleRateHz = 96000 };
        var display = SourceRatePresentation.Describe(source, settings, observation,
            playback: FavoritePlaybackState.NotPlaying);

        Assert.Equal("96 kHz est.", display.Text);
        Assert.Contains("Output held during pause", display.Detail);
        Assert.Equal(observation.Detail, display.Evidence);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingAppleTrackEvidenceNeverDisplaysHeldOutputAsTheTrackRate(bool active)
    {
        var source = AppleSource() with { IsActive = active };
        var settings = Settings(source);
        settings.LastKnownSourceKey = source.Identity;
        settings.LastKnownSampleRateHz = 44100;
        var display = SourceRatePresentation.Describe(source, settings,
            Observation(source) with { SessionToken = "old-session", SampleRateHz = 96000 },
            StreamObservation(source) with { SampleRateHz = 44100 });

        Assert.Equal("Estimate pending", display.Text);
        Assert.Contains("Last confirmed output: 44.1 kHz", display.Detail);
        Assert.Null(display.Evidence);
    }

    [Fact]
    public void PausedMasterDisplaysItsConfirmedHeldRateWithoutClaimingAnActiveStream()
    {
        var source = GenericSource() with { IsActive = false };
        var settings = StreamSettings(source);
        settings.LastKnownSourceKey = source.Identity.ToLowerInvariant();
        settings.LastKnownSampleRateHz = 96000;

        var display = SourceRatePresentation.Describe(source, settings, StreamObservation(source));

        Assert.Equal("96 kHz held", display.Text);
        Assert.Contains("last confirmed output rate", display.Detail);
        Assert.Null(display.Evidence);
    }

    [Fact]
    public void PausedMasterDoesNotDisplayAnotherAppsHeldRate()
    {
        var source = GenericSource() with { IsActive = false };
        var settings = StreamSettings(source);
        settings.LastKnownSourceKey = AppleSource().Identity;
        settings.LastKnownSampleRateHz = 96000;

        var display = SourceRatePresentation.Describe(source, settings);

        Assert.Equal("Rate held", display.Text);
        Assert.Contains("Current output rate is unchanged", display.Detail);
        Assert.Null(display.Evidence);
    }

    [Fact]
    public void IdleNonmasterDoesNotClaimTheMastersHeldRate()
    {
        var source = GenericSource() with { IsActive = false };
        var master = AppleSource();
        var settings = StreamSettings(master);
        settings.LastKnownSourceKey = master.Identity;
        settings.LastKnownSampleRateHz = 96000;

        var display = SourceRatePresentation.Describe(source, settings, StreamObservation(source));

        Assert.Equal("Idle / paused", display.Text);
        Assert.Contains("no active stream rate", display.Detail);
        Assert.Null(display.Evidence);
    }

    [Theory]
    [InlineData(7999)]
    [InlineData(768001)]
    public void InvalidHeldRateCannotBecomeANumericTargetLabel(int rate)
    {
        var source = GenericSource() with { IsActive = false };
        var settings = StreamSettings(source);
        settings.LastKnownSourceKey = source.Identity;
        settings.LastKnownSampleRateHz = rate;

        Assert.Equal("Rate held", SourceRatePresentation.Describe(source, settings).Text);
    }

    [Fact]
    public void OptOutImmediatelyHidesPreviousEstimate()
    {
        var source = AppleSource();
        var settings = Settings(source);
        settings.CatalogRateDetectionEnabled = false;
        var display = SourceRatePresentation.Describe(source, settings, Observation(source));

        Assert.Equal("Estimate off", display.Text);
        Assert.Null(display.Evidence);
    }

    [Fact]
    public void UnselectedAppleMusicShowsItsOwnCurrentEstimateIndependentOfMaster()
    {
        var source = AppleSource();
        var display = SourceRatePresentation.Describe(source, Settings(GenericSource()), Observation(source));

        Assert.Equal("44.1 kHz est.", display.Text);
        Assert.Contains("independent of the master and mixer", display.Detail);
        Assert.Equal(Observation(source).Detail, display.Evidence);
    }

    [Fact]
    public void MissingSessionIdentityCannotDisplayAnEstimate()
    {
        var source = AppleSource() with { SessionToken = "" };
        Assert.Equal("Estimate pending", SourceRatePresentation.Describe(source, Settings(source), Observation(source)).Text);
    }

    [Fact]
    public void AutoCaptionStatesTheSelectedSourcesActualSupport()
    {
        var generic = GenericSource();
        var apple = AppleSource();
        var settings = Settings(apple);

        Assert.Equal("No automatic detector — choose a manual rate", SourceRatePresentation.AutoModeText(generic, Settings(generic)));
        Assert.Equal("Auto — Apple Music track format", SourceRatePresentation.AutoModeText(apple, settings));
        settings.CatalogRateDetectionEnabled = false;
        Assert.Equal("Apple Music track format off — choose a manual rate", SourceRatePresentation.AutoModeText(apple, settings));
        Assert.Equal("Hold output / choose a manual rate", SourceRatePresentation.AutoModeText(null, settings));
        Assert.Equal("Hold output / choose a manual rate", SourceRatePresentation.AutoModeText(generic, settings));
    }

    [Fact]
    public void CurrentWasapiObservationDisplaysSubmittedStreamRateAndPreservesEvidence()
    {
        var source = GenericSource();
        var observation = StreamObservation(source);
        var display = SourceRatePresentation.Describe(source, StreamSettings(source), observation);

        Assert.Equal("48 kHz stream", display.Text);
        Assert.Contains("Actual PCM rate submitted to WASAPI", display.Detail);
        Assert.Contains("original media rate remains unverified", display.Detail);
        Assert.Equal(observation.Detail, display.Evidence);
    }

    [Fact]
    public void PassiveStreamRateCanBeDisplayedForAnUnselectedAppWithoutLeakingToAnotherRow()
    {
        var source = GenericSource();
        var master = AppleSource();
        var settings = StreamSettings(master);
        var observation = StreamObservation(source);

        Assert.Equal("48 kHz stream", SourceRatePresentation.Describe(source, settings, streamObservation: observation).Text);
        Assert.Equal("Estimate pending", SourceRatePresentation.Describe(master, settings, streamObservation: observation).Text);
    }

    [Fact]
    public void ManualTargetTakesPriorityOverSubmittedStreamRate()
    {
        var source = GenericSource();
        var settings = StreamSettings(source);
        settings.ManualSampleRatesHz[source.Identity] = 44100;

        var display = SourceRatePresentation.Describe(source, settings, StreamObservation(source));

        Assert.Equal("44.1 kHz manual", display.Text);
        Assert.Null(display.Evidence);
    }

    [Fact]
    public void ExplicitAppleCatalogModeTakesPriorityOverTheAlreadyResampledWindowsStream()
    {
        var source = AppleSource();
        var display = SourceRatePresentation.Describe(source, StreamSettings(source), Observation(source), StreamObservation(source));

        Assert.Equal("44.1 kHz est.", display.Text);
        Assert.Equal(Observation(source).Detail, display.Evidence);
    }

    [Fact]
    public void CurrentCatalogEstimateRemainsAvailableWhileWaitingForAStreamObservation()
    {
        var source = AppleSource();
        var invalidStream = StreamObservation(source) with { SessionToken = "old-stream-group" };

        var display = SourceRatePresentation.Describe(source, StreamSettings(source), Observation(source), invalidStream);

        Assert.Equal("44.1 kHz est.", display.Text);
        Assert.Equal(Observation(source).Detail, display.Evidence);
    }

    [Theory]
    [InlineData("different-source")]
    [InlineData("different-session")]
    [InlineData("old-selection")]
    [InlineData("missing-rate")]
    [InlineData("rate-too-low")]
    [InlineData("rate-too-high")]
    [InlineData("inactive")]
    [InlineData("missing-session")]
    public void InvalidStreamObservationIsNeverDisplayedAsDetected(string mismatch)
    {
        var source = GenericSource();
        var observation = StreamObservation(source);
        observation = mismatch switch
        {
            "different-source" => observation with { SourceKey = "OTHER" },
            "different-session" => observation with { SessionToken = "old-stream-group" },
            "old-selection" => observation with { SelectionRevision = 1 },
            "missing-rate" => observation with { SampleRateHz = null },
            "rate-too-low" => observation with { SampleRateHz = 7999 },
            "rate-too-high" => observation with { SampleRateHz = 768001 },
            _ => observation
        };
        if (mismatch == "inactive") source = source with { IsActive = false };
        if (mismatch == "missing-session")
        {
            source = source with { SessionToken = "" };
            observation = observation with { SessionToken = "" };
        }

        var display = SourceRatePresentation.Describe(source, StreamSettings(source), observation);

        Assert.Equal(mismatch == "inactive" ? "Rate held" : "Waiting for stream", display.Text);
        Assert.Null(display.Evidence);
    }

    [Theory]
    [InlineData(RateProvenance.Unknown)]
    [InlineData(RateProvenance.WindowsMixFormat)]
    [InlineData(RateProvenance.ApplicationReported)]
    [InlineData(RateProvenance.CatalogMetadata)]
    [InlineData(RateProvenance.VerifiedNative)]
    public void UnrelatedEvidenceCannotBecomeAnObservedWasapiStream(RateProvenance provenance)
    {
        var source = GenericSource();
        var observation = StreamObservation(source) with { Provenance = provenance };

        var display = SourceRatePresentation.Describe(source, StreamSettings(source), observation);

        Assert.Equal("Waiting for stream", display.Text);
        Assert.Null(display.Evidence);
    }

    [Fact]
    public void DisablingStreamDetectionImmediatelyHidesCapturedRates()
    {
        var source = GenericSource();
        var settings = StreamSettings(source);
        settings.StreamRateDetectionEnabled = false;
        var display = SourceRatePresentation.Describe(source, settings, StreamObservation(source));

        Assert.Equal("No detector", display.Text);
        Assert.Null(display.Evidence);
    }

    [Fact]
    public void MissingStreamProvidesExistingStreamRetryGuidance()
    {
        var source = GenericSource();
        var settings = StreamSettings(source);
        var display = SourceRatePresentation.Describe(source, settings);

        Assert.Equal("Waiting for stream", display.Text);
        Assert.Contains("Refresh retries existing-stream detection", display.Detail);
        Assert.Equal("Auto — application stream rate (WASAPI)", SourceRatePresentation.AutoModeText(source, settings));
        Assert.Equal("Auto — Apple Music track format", SourceRatePresentation.AutoModeText(AppleSource(), StreamSettings(AppleSource())));
    }

    private static PlatformAudioSource AppleSource() => new("APPLE", "AppleMusic",
        @"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.2.3.4_x64__nzyj5cx40ttqa\AppleMusic.exe",
        [123], true, true, "current-session");

    private static PlatformAudioSource GenericSource() => new("SPOTIFY", "Spotify",
        @"C:\Apps\Spotify.exe", [456], true, true, "spotify-session");

    private static SourceSettings Settings(PlatformAudioSource source) => new()
    {
        SelectedSourceKey = source.Identity,
        SelectedSourceDisplayName = source.ProcessName,
        SelectionRevision = 2,
        StreamRateDetectionEnabled = false,
        CatalogRateDetectionEnabled = true
    };

    private static SourceSettings StreamSettings(PlatformAudioSource source)
    {
        var settings = Settings(source);
        settings.StreamRateDetectionEnabled = true;
        return settings;
    }

    private static RateObservation Observation(PlatformAudioSource source) => new(source.Identity,
        source.SessionToken, 2, 44100, RateProvenance.CatalogMetadata, "Available lossless catalog asset: 44.1 kHz.");

    private static RateObservation StreamObservation(PlatformAudioSource source) => new(source.Identity,
        source.SessionToken, 2, 48000, RateProvenance.WasapiStream, "Observed PCM format on the selected output for this app's current stream group.");
}

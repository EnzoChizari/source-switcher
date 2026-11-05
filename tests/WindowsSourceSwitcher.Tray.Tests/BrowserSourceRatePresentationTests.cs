using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class BrowserSourceRatePresentationTests
{
    private const string Browser = @"C:\Program Files\Zen Browser\zen.exe";

    [Fact]
    public void CurrentChosenTabEvidenceKeepsDecoderAndOutputProvenanceSeparate()
    {
        var display = SourceRatePresentation.Describe(Source(), Settings(), Decoder(), Stream());
        Assert.Equal("96 kHz tab decoder", display.Text);
        Assert.Contains("explicitly chosen", display.Detail);
        Assert.Contains("routing", display.Detail);
        Assert.Contains("unverified", display.Detail);
        Assert.Equal("Selected-tab decoder metadata", display.Evidence);
    }

    [Fact]
    public void DirectMediaEvidenceIsLabeledAsFileHeader()
    {
        var snapshot = Snapshot() with { EvidenceField = BrowserWireProtocol.ChromiumHeaderEvidenceField };
        var display = SourceRatePresentation.Describe(Source(), Settings(), Decoder(), Stream(), snapshot);
        Assert.Equal("96 kHz file header", display.Text);
        Assert.Contains("Windows output", display.Detail);
        Assert.Equal("24-bit / 96 kHz file header",
            SourceRatePresentation.Describe(Source(), Settings(), Decoder() with { BitsPerSample = 24 }, Stream(), snapshot).Text);
    }

    [Fact]
    public void ManualTargetWinsOverTabDecoderAndPinnedBrowserHold()
    {
        var settings = Settings();
        settings.ManualSampleRatesHz[Browser] = 88200;
        var display = SourceRatePresentation.Describe(Source(), settings, Decoder(), Stream(), Snapshot());
        Assert.Equal("88.2 kHz manual", display.Text);
        Assert.Null(display.Evidence);
    }

    [Fact]
    public void DisablingBrowserIntegrationImmediatelyRemovesPreviousDecoderLabel()
    {
        var settings = Settings();
        settings.BrowserRateDetectionEnabled = false;
        var display = SourceRatePresentation.Describe(Source(), settings, Decoder(), Stream(), Snapshot());
        Assert.Equal("48 kHz stream", display.Text);
        Assert.Equal("Actual submitted PCM", display.Evidence);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("session")]
    [InlineData("revision")]
    [InlineData("rate-missing")]
    [InlineData("rate-low")]
    [InlineData("rate-high")]
    public void StaleOrInvalidDecoderMetadataCannotBePresentedAsCurrentRate(string mismatch)
    {
        var observation = Decoder();
        observation = mismatch switch
        {
            "source" => observation with { SourceKey = @"C:\Other\zen.exe" },
            "session" => observation with { SessionToken = "old-session" },
            "revision" => observation with { SelectionRevision = 1 },
            "rate-missing" => observation with { SampleRateHz = null },
            "rate-low" => observation with { SampleRateHz = 7999 },
            _ => observation with { SampleRateHz = 768001 }
        };
        var display = SourceRatePresentation.Describe(Source(), Settings(), observation, Stream());
        Assert.Equal("48 kHz stream", display.Text);
        Assert.Equal("Actual submitted PCM", display.Evidence);
    }

    [Theory]
    [InlineData(BrowserDecoderState.Paused)]
    [InlineData(BrowserDecoderState.Unavailable)]
    [InlineData(BrowserDecoderState.Disconnected)]
    [InlineData(BrowserDecoderState.TimedOut)]
    public void SupportedPinnedAdapterHoldsRatherThanDisplayingItsResampledOutput(BrowserDecoderState state)
    {
        var snapshot = Snapshot() with { State = state, DecoderRateHz = null, Detail = "Decoder unavailable; holding" };
        var display = SourceRatePresentation.Describe(Source(), Settings(), streamObservation: Stream(), browser: snapshot);
        Assert.Equal("44.1 kHz held", display.Text);
        Assert.Equal(snapshot.Detail, display.Detail);
        Assert.Null(display.Evidence);
    }

    [Fact]
    public void UnsupportedAdapterCanDisplaySeparateActualWindowsPcmRate()
    {
        var snapshot = Snapshot() with { DecoderSupported = false, State = BrowserDecoderState.Unavailable, DecoderRateHz = null };
        var display = SourceRatePresentation.Describe(Source(), Settings(), streamObservation: Stream(), browser: snapshot);
        Assert.Equal("48 kHz stream", display.Text);
        Assert.Contains("original media rate remains unverified", display.Detail);
        Assert.Equal("Actual submitted PCM", display.Evidence);
    }

    [Fact]
    public void BrowserHoldWithoutConfirmedHistoryDoesNotDisplayAClaimedRate()
    {
        var settings = Settings();
        settings.LastKnownSourceKey = null;
        settings.LastKnownSampleRateHz = null;
        var display = SourceRatePresentation.Describe(Source(), settings, streamObservation: Stream(), browser: Snapshot());
        Assert.Equal("Tab rate held", display.Text);
        Assert.Null(display.Evidence);
    }

    [Theory]
    [InlineData(7999)]
    [InlineData(768001)]
    public void InvalidBrowserHeldRateCannotBecomeANumericTargetLabel(int held)
    {
        var settings = Settings();
        settings.LastKnownSampleRateHz = held;
        var display = SourceRatePresentation.Describe(Source(), settings, browser: Snapshot());
        Assert.Equal("Tab rate held", display.Text);
        Assert.Null(display.Evidence);
    }

    [Fact]
    public void PinnedMasterHoldDoesNotHideAnotherApplicationsOwnStream()
    {
        var other = Source() with { Identity = @"C:\Apps\Spotify.exe", ProcessName = "Spotify", SessionToken = "spotify" };
        var stream = Stream() with { SourceKey = other.Identity, SessionToken = other.SessionToken };
        var display = SourceRatePresentation.Describe(other, Settings(), streamObservation: stream, browser: Snapshot());
        Assert.Equal("48 kHz stream", display.Text);
        Assert.Equal("Actual submitted PCM", display.Evidence);
    }

    private static PlatformAudioSource Source() => new(Browser, "Zen", Browser, [100, 200], true, true, "current-session");
    private static SourceSettings Settings() => new()
    {
        BrowserRateDetectionEnabled = true,
        StreamRateDetectionEnabled = true,
        SelectedSourceKey = Browser,
        SelectedSourceDisplayName = "Zen",
        SelectionRevision = 2,
        LastKnownSourceKey = Browser,
        LastKnownSampleRateHz = 44100
    };
    private static RateObservation Decoder() => new(Browser, "current-session", 2, 96000,
        RateProvenance.BrowserDecoder, "Selected-tab decoder metadata");
    private static RateObservation Stream() => new(Browser, "current-session", 2, 48000,
        RateProvenance.WasapiStream, "Actual submitted PCM");
    private static BrowserDecoderSnapshot Snapshot() => new(1, Browser, 100,
        DateTimeOffset.Parse("2026-10-20T00:00:00Z"), true, 7, "document", 1, false, 96000, 48000,
        BrowserDecoderState.Available, "Chosen decoder evidence", 1234, "browser-lifetime", "Tab");
}

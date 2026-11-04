using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class SpotifyFileQualityReaderTests
{
    [Theory]
    [InlineData("Lossless 16-bit", "File quality", "Up to 16-bit/44.1 kHz • FLAC", 16)]
    [InlineData("Lossless 24-bit", "File quality", "Up to 24-bit/44.1 kHz • FLAC", 24)]
    [InlineData("Lossless", "File quality", "Up to 24-bit/44.1 kHz • FLAC", null)]
    [InlineData("Lossless 24-bit", "Media quality", "Up to 24-bit/44.1 kHz • FLAC", null)]
    [InlineData("Lossless 24-bit", "File quality", "Up to 16-bit/44.1 kHz • FLAC", null)]
    public void RequiresMatchingTrackQualityAndFlacDetails(string heading, string label, string detail, int? expected)
    {
        Assert.Equal(expected, SpotifyFileQualityReader.ReadCardDepth(heading, text => text == label || text == detail));
    }
}

using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Core.Tests;

public sealed class EndpointFormatPolicyTests
{
    [Fact]
    public void ExactRateSelectionPreservesCurrentDepthAndChannelCount()
    {
        EndpointFormat current = new(48000, 24, 2);
        EndpointFormat expected = new(96000, 24, 2);
        EndpointFormat[] formats = [new(96000, 16, 2), new(96000, 24, 6), expected];
        Assert.Same(expected, EndpointFormatPolicy.SelectExact(current, formats, 96000));
    }

    [Fact]
    public void UnsupportedRateNeverFallsBackToNearestRate()
    {
        EndpointFormat current = new(48000, 32, 2);
        EndpointFormat[] formats = [new(88200, 32, 2), new(96000, 32, 2), new(86000, 16, 2)];
        Assert.Null(EndpointFormatPolicy.SelectExact(current, formats, 86000));
    }

    [Theory]
    [InlineData(44100, 44100, 16, 24, 24)]
    [InlineData(44100, 44100, 24, 16, 24)]
    [InlineData(44100, 96000, 32, 24, 32)]
    public void CatalogDepthCanRaiseOutputButCannotLowerIt(int currentRate, int targetRate,
        int currentDepth, int estimate, int targetDepth)
    {
        EndpointFormat current = new(currentRate, currentDepth, 2);
        EndpointFormat[] formats = [new(targetRate, 16, 2), new(targetRate, 24, 2), new(targetRate, 32, 2)];
        Assert.Equal(new EndpointFormat(targetRate, targetDepth, 2),
            EndpointFormatPolicy.SelectExact(current, formats, targetRate, estimate, preventDepthReduction: true));
    }

    [Fact]
    public void CatalogDepthKeepsCurrentPrecisionWhenTheDeviceCannotRaiseIt()
    {
        EndpointFormat current = new(44100, 16, 2);
        EndpointFormat supported = new(96000, 16, 2);
        Assert.Equal(supported, EndpointFormatPolicy.SelectExact(current, [supported], 96000, 24, preventDepthReduction: true));
        Assert.Null(EndpointFormatPolicy.SelectExact(current with { BitsPerSample = 32 }, [supported],
            96000, 24, preventDepthReduction: true));
    }
}

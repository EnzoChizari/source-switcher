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
}

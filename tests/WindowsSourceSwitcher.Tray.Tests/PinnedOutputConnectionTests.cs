using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class PinnedOutputConnectionTests
{
    [Fact]
    public void AnIncompleteEndpointCensusCannotProveDisconnectionButCanProvePresence()
    {
        Assert.Null(PinnedOutputConnection.AvailabilityFromCensus(false, "Audio service read failed"));
        Assert.False(PinnedOutputConnection.AvailabilityFromCensus(false, null));
        Assert.True(PinnedOutputConnection.AvailabilityFromCensus(true, "Another endpoint changed"));
    }
    [Fact]
    public void InitiallyMissingOutputWaitsAndReconnectReportsObservedOutageOnce()
    {
        var tracker = new PinnedOutputConnection();
        Assert.Equal(OutputConnectionState.Waiting, tracker.Observe("dac", false, TimeSpan.Zero)!.State);
        Assert.Null(tracker.Observe("dac", false, TimeSpan.FromSeconds(2)));
        var recovery = tracker.Observe("dac", true, TimeSpan.FromSeconds(5));
        Assert.Equal(OutputConnectionState.Available, recovery!.State);
        Assert.Contains("5.0 seconds", recovery.Detail);
        Assert.Null(tracker.Observe("dac", true, TimeSpan.FromSeconds(9)));
    }

    [Fact]
    public void KnownOutputDisconnectsAndAReadFailureIsNotAnotherDisconnection()
    {
        var tracker = new PinnedOutputConnection();
        tracker.Observe("dac", true, TimeSpan.Zero);
        Assert.Equal(OutputConnectionState.Unknown, tracker.Observe("dac", null, TimeSpan.FromSeconds(1))!.State);
        Assert.Null(tracker.Observe("dac", null, TimeSpan.FromSeconds(2)));
        Assert.Equal(OutputConnectionState.Disconnected, tracker.Observe("dac", false, TimeSpan.FromSeconds(3))!.State);
        Assert.Contains("retained", tracker.Detail);
        tracker.Observe("dac", null, TimeSpan.FromSeconds(4));
        Assert.Contains("3.0 seconds", tracker.Observe("dac", true, TimeSpan.FromSeconds(6))!.Detail);
    }

    [Fact]
    public void ChangingThePinnedOutputDoesNotTransferAnotherDevicesOutage()
    {
        var tracker = new PinnedOutputConnection();
        tracker.Observe("first", true, TimeSpan.Zero);
        tracker.Observe("first", false, TimeSpan.FromSeconds(1));
        Assert.Equal(OutputConnectionState.Waiting, tracker.Observe("second", false, TimeSpan.FromSeconds(2))!.State);
        Assert.Contains("2.0 seconds", tracker.Observe("second", true, TimeSpan.FromSeconds(4))!.Detail);
        Assert.Equal(OutputConnectionState.None, tracker.Observe(null, null, TimeSpan.FromSeconds(5))!.State);
    }

    [Fact]
    public void ReversedClockCannotProduceNegativeOutageDurationAndCaseDoesNotChangeIdentity()
    {
        var tracker = new PinnedOutputConnection();
        tracker.Observe("DAC", true, TimeSpan.FromSeconds(20));
        tracker.Observe("dac", false, TimeSpan.FromSeconds(21));
        Assert.Equal(OutputConnectionState.Available, tracker.Observe("DAC", true, TimeSpan.Zero)!.State);
        Assert.DoesNotContain("seconds", tracker.Detail);
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Observe("dac", true, TimeSpan.FromTicks(-1)));
    }
}

using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Core.Tests;

public sealed class StreamRateTrackerTests
{
    private static readonly DateTimeOffset ProcessStart = new(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Observed = ProcessStart.AddMinutes(1);
    private static readonly Guid Client = Guid.Parse("e03dca4c-23a7-4ad9-9b53-070b775a1803");
    private const string Endpoint = "{0.0.0.00000000}.{TOPPING}";
    private static readonly StreamProcessIdentity[] Current = [new(100, ProcessStart)];

    [Fact]
    public void RequiresStartAndHoldsNoSavedRateWhilePaused()
    {
        var tracker = new StreamRateTracker();
        Assert.True(tracker.Initialize(Format()));
        Assert.Null(tracker.Resolve(Endpoint, Current).SampleRateHz);
        Assert.True(tracker.SetPlaying(100, Client, true, Observed.AddSeconds(1)));
        Assert.Equal(44_100, tracker.Resolve(Endpoint, Current).SampleRateHz);
        Assert.True(tracker.SetPlaying(100, Client, false, Observed.AddSeconds(2)));
        Assert.Null(tracker.Resolve(Endpoint, Current).SampleRateHz);
        Assert.True(tracker.SetPlaying(100, Client, true, Observed.AddSeconds(3)));
        Assert.Equal(44_100, tracker.Resolve(Endpoint, Current).SampleRateHz);
    }

    [Fact]
    public void StartWithoutInitializeProvidesNoEvidence()
    {
        var tracker = new StreamRateTracker();
        Assert.False(tracker.SetPlaying(100, Client, true, Observed));
        Assert.Null(tracker.Resolve(Endpoint, Current).SampleRateHz);
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void SimultaneousConflictingRatesReturnUnknownUntilOneStops()
    {
        var tracker = PlayingTracker();
        var secondClient = Guid.NewGuid();
        tracker.Initialize(Format() with { ClientId = secondClient, SampleRateHz = 48_000 });
        tracker.SetPlaying(100, secondClient, true, Observed.AddSeconds(1));
        var conflict = tracker.Resolve(Endpoint, Current);
        Assert.Null(conflict.SampleRateHz);
        Assert.Contains("conflicting", conflict.Detail);
        tracker.SetPlaying(100, secondClient, false, Observed.AddSeconds(2));
        Assert.Equal(44_100, tracker.Resolve(Endpoint, Current).SampleRateHz);
    }

    [Fact]
    public void MultipleProcessesForSourceMustAgree()
    {
        var tracker = PlayingTracker();
        var secondClient = Guid.NewGuid();
        tracker.Initialize(Format() with { ProcessId = 200, ClientId = secondClient });
        tracker.SetPlaying(200, secondClient, true, Observed.AddSeconds(1));
        StreamProcessIdentity[] both = [..Current, new(200, ProcessStart)];
        Assert.Equal(44_100, tracker.Resolve(Endpoint, both).SampleRateHz);
        tracker.Initialize(Format() with { ProcessId = 200, ClientId = secondClient,
            SampleRateHz = 48_000, ObservedAtUtc = Observed.AddSeconds(2) });
        tracker.SetPlaying(200, secondClient, true, Observed.AddSeconds(3));
        Assert.Null(tracker.Resolve(Endpoint, both).SampleRateHz);
        Assert.Equal(44_100, tracker.Resolve(Endpoint, Current).SampleRateHz);
    }

    [Fact]
    public void OtherEndpointAndOtherProcessesDoNotContaminateSelectedSource()
    {
        var tracker = PlayingTracker();
        var other = Guid.NewGuid();
        tracker.Initialize(Format() with { ClientId = other, EndpointId = "other", SampleRateHz = 96_000 });
        tracker.SetPlaying(100, other, true, Observed.AddSeconds(1));
        Assert.Equal(44_100, tracker.Resolve(Endpoint, Current).SampleRateHz);
        Assert.Equal(96_000, tracker.Resolve("other", Current).SampleRateHz);
        Assert.Null(tracker.Resolve("unseen", Current).SampleRateHz);
        Assert.Null(tracker.Resolve(Endpoint, [new(200, ProcessStart)]).SampleRateHz);
    }

    [Fact]
    public void ReusedPidDoesNotInheritPreviousProcessEvidence()
    {
        var tracker = PlayingTracker();
        var restartedAt = ProcessStart.AddSeconds(30);
        StreamProcessIdentity[] restarted = [new(100, restartedAt)];
        Assert.Null(tracker.Resolve(Endpoint, restarted).SampleRateHz);
        tracker.Initialize(Format() with { ProcessStartedAtUtc = restartedAt,
            ObservedAtUtc = Observed.AddSeconds(2), SampleRateHz = 48_000 });
        tracker.SetPlaying(100, Client, true, Observed.AddSeconds(3));
        Assert.Equal(48_000, tracker.Resolve(Endpoint, restarted).SampleRateHz);
        Assert.Null(tracker.Resolve(Endpoint, Current).SampleRateHz);
    }

    [Fact]
    public void OlderEventsCannotResurrectStoppedStreamOrOverwriteNewFormat()
    {
        var tracker = PlayingTracker();
        tracker.SetPlaying(100, Client, false, Observed.AddSeconds(3));
        Assert.False(tracker.SetPlaying(100, Client, true, Observed.AddSeconds(2)));
        Assert.False(tracker.Initialize(Format() with { SampleRateHz = 96_000 }));
        Assert.Null(tracker.Resolve(Endpoint, Current).SampleRateHz);
        tracker.Initialize(Format() with { SampleRateHz = 48_000, ObservedAtUtc = Observed.AddSeconds(4) });
        Assert.False(tracker.SetPlaying(100, Client, true, Observed.AddSeconds(3)));
        tracker.SetPlaying(100, Client, true, Observed.AddSeconds(5));
        Assert.Equal(48_000, tracker.Resolve(Endpoint, Current).SampleRateHz);
        Assert.False(tracker.SetPlaying(100, Client, false, Observed.AddSeconds(4)));
        Assert.Equal(48_000, tracker.Resolve(Endpoint, Current).SampleRateHz);
    }

    [Fact]
    public void ReinitializationRequiresAnotherStart()
    {
        var tracker = PlayingTracker();
        tracker.Initialize(Format() with { SampleRateHz = 48_000, ObservedAtUtc = Observed.AddSeconds(2) });
        Assert.Null(tracker.Resolve(Endpoint, Current).SampleRateHz);
        tracker.SetPlaying(100, Client, true, Observed.AddSeconds(3));
        Assert.Equal(48_000, tracker.Resolve(Endpoint, Current).SampleRateHz);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(7999)]
    [InlineData(768001)]
    public void InvalidRatesAreRejected(int rate)
    {
        var tracker = new StreamRateTracker();
        Assert.False(tracker.Initialize(Format() with { SampleRateHz = rate }));
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void InvalidIdentityAndTimesAreRejectedWithoutChangingEvidence()
    {
        var tracker = PlayingTracker();
        Assert.False(tracker.Initialize(Format() with { ProcessId = 0 }));
        Assert.False(tracker.Initialize(Format() with { ProcessId = -1 }));
        Assert.False(tracker.Initialize(Format() with { ClientId = Guid.Empty }));
        Assert.False(tracker.Initialize(Format() with { EndpointId = " " }));
        Assert.False(tracker.Initialize(Format() with { ProcessStartedAtUtc = default }));
        Assert.False(tracker.Initialize(Format() with { ObservedAtUtc = ProcessStart.AddSeconds(-1) }));
        Assert.False(tracker.SetPlaying(0, Client, true, Observed));
        Assert.False(tracker.SetPlaying(100, Guid.Empty, true, Observed));
        Assert.Equal(44_100, tracker.Resolve(Endpoint, Current).SampleRateHz);
        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void CapacityIsBoundedAndOverflowWithholdsIncompleteEvidenceUntilClear()
    {
        var tracker = PlayingTracker();
        for (var i = 1; i < StreamRateTracker.MaximumStreams; i++)
        {
            Assert.True(tracker.Initialize(Format() with { ProcessId = 100 + i }));
            Assert.True(tracker.SetPlaying(100 + i, Client, true, Observed.AddSeconds(1)));
        }
        Assert.False(tracker.Initialize(Format() with { ProcessId = 9999 }));
        Assert.Equal(StreamRateTracker.MaximumStreams, tracker.Count);
        Assert.Null(tracker.Resolve(Endpoint, Current).SampleRateHz);
        Assert.Contains("capacity", tracker.Resolve(Endpoint, Current).Detail);
        tracker.Clear();
        Assert.Equal(0, tracker.Count);
        tracker.Initialize(Format());
        tracker.SetPlaying(100, Client, true, Observed.AddSeconds(1));
        Assert.Equal(44_100, tracker.Resolve(Endpoint, Current).SampleRateHz);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapacityReclaimsOldestInactiveStreamWithoutDiscardingPlayingStreams(bool wasPlaying)
    {
        var tracker = PlayingTracker();
        const int oldestPid = 2000;
        tracker.Initialize(Format() with { ProcessId = oldestPid });
        if (wasPlaying)
        {
            tracker.SetPlaying(oldestPid, Client, true, Observed.AddMilliseconds(100));
            tracker.SetPlaying(oldestPid, Client, false, Observed.AddMilliseconds(200));
        }
        for (var i = 2; i < StreamRateTracker.MaximumStreams; i++)
            tracker.Initialize(Format() with { ProcessId = 2000 + i, ObservedAtUtc = Observed.AddSeconds(1) });

        const int newPid = 9999;
        Assert.True(tracker.Initialize(Format() with { ProcessId = newPid, SampleRateHz = 48_000,
            ObservedAtUtc = Observed.AddSeconds(2) }));
        Assert.Equal(StreamRateTracker.MaximumStreams, tracker.Count);
        Assert.False(tracker.SetPlaying(oldestPid, Client, true, Observed.AddSeconds(3)));
        Assert.Null(tracker.Resolve(Endpoint, [new(oldestPid, ProcessStart)]).SampleRateHz);
        Assert.True(tracker.SetPlaying(newPid, Client, true, Observed.AddSeconds(3)));
        Assert.Equal(48_000, tracker.Resolve(Endpoint, [new(newPid, ProcessStart)]).SampleRateHz);
        Assert.Equal(44_100, tracker.Resolve(Endpoint, Current).SampleRateHz);
    }

    [Fact]
    public void ClearDiscardsEvidenceAndUninitializedEventsRemainIgnored()
    {
        var tracker = PlayingTracker();
        tracker.Clear();
        Assert.False(tracker.SetPlaying(100, Client, true, Observed.AddSeconds(2)));
        Assert.Null(tracker.Resolve(Endpoint, Current).SampleRateHz);
    }

    private static AudioStreamFormat Format() => new(100, ProcessStart, Client, Endpoint, 44_100, Observed);

    private static StreamRateTracker PlayingTracker()
    {
        var tracker = new StreamRateTracker();
        tracker.Initialize(Format());
        tracker.SetPlaying(100, Client, true, Observed.AddSeconds(1));
        return tracker;
    }
}

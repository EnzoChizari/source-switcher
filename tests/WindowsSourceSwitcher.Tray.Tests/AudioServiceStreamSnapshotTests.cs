using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class AudioServiceStreamSnapshotTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddMilliseconds(50);

    [Fact]
    public void EmptyRundownDoesNotClaimProviderWasObserved()
    {
        var result = new AudioServiceSnapshotAccumulator().Complete(Start, End);
        Assert.Empty(result.Streams);
        Assert.False(result.WasObserved);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    [InlineData(192000)]
    public void CompleteRundownKeepsActualSourceRateAndCaptureInterval(int rate)
    {
        var accumulator = new AudioServiceSnapshotAccumulator();
        Add(accumulator, rate: rate);
        var result = accumulator.Complete(Start, End);
        Assert.Equal(rate, Assert.Single(result.Streams).SampleRateHz);
        Assert.Equal(Start, result.StartedAtUtc);
        Assert.Equal(End, result.CompletedAtUtc);
        Assert.True(result.WasObserved);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ForeignAndOutOfCaptureWindowEventsAreNotEvidence()
    {
        var accumulator = new AudioServiceSnapshotAccumulator();
        accumulator.Add(Guid.NewGuid(), "AudioSrvStreamState", 42, Start, Values());
        accumulator.Add(AudioServiceStreamEventParser.ProviderId, "AudioSrvStreamState_PolicyVolume", 42, Start, Values());
        Add(accumulator, at: Start.AddTicks(-1));
        Add(accumulator, at: End.AddTicks(1));
        var result = accumulator.Complete(Start, End);
        Assert.Empty(result.Streams);
        Assert.False(result.WasObserved);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    public void LostEventsInvalidateTheEntireResult(int lost)
    {
        var accumulator = new AudioServiceSnapshotAccumulator();
        Add(accumulator);
        var result = accumulator.Complete(Start, End, lost);
        Assert.Empty(result.Streams);
        Assert.NotNull(result.Error);
        Assert.True(result.WasObserved);
    }

    [Fact]
    public void NativeCaptureFailureCannotPublishPartialEvidence()
    {
        var accumulator = new AudioServiceSnapshotAccumulator();
        Add(accumulator);
        var result = accumulator.Complete(Start, End, failure: "Enable timed out.");
        Assert.Equal("Enable timed out.", result.Error);
        Assert.Empty(result.Streams);
    }

    [Fact]
    public void KnownProcessWithUnsupportedOrMalformedStreamBlocksOnlyItsOwnSource()
    {
        var accumulator = new AudioServiceSnapshotAccumulator();
        Add(accumulator, processId: 91, streamId: 1);
        Add(accumulator, processId: 92, streamId: 2);
        var malformed = Values(91, 3);
        malformed.Remove("SampleRate");
        AddValues(accumulator, malformed);
        var result = accumulator.Complete(Start, End);
        Assert.Null(result.Error);
        Assert.Equal([91], result.InvalidProcessIds);
        Assert.Equal(92, Assert.Single(result.Streams).SourceProcessId);
    }

    [Theory]
    [InlineData("EndpointConnectorType", 1)]
    [InlineData("AudioClientShareMode", 1)]
    [InlineData("ShareMode", 1)]
    [InlineData("StreamFlags", 0x20000)]
    public void KnownNonSharedRenderStreamDoesNotInvalidateValidRenderFromSameProcess(string field, int value)
    {
        var accumulator = new AudioServiceSnapshotAccumulator();
        Add(accumulator);
        var nonCandidate = Values(streamId: 2);
        nonCandidate[field] = value;
        AddValues(accumulator, nonCandidate);
        var result = accumulator.Complete(Start, End);
        Assert.Null(result.Error);
        Assert.Empty(result.InvalidProcessIds);
        Assert.Single(result.Streams);
    }

    [Theory]
    [InlineData("EndpointConnectorType", "1")]
    [InlineData("EndpointConnectorType", 2)]
    [InlineData("ShareMode", "1")]
    [InlineData("ShareMode", -1)]
    [InlineData("StreamFlags", "131072")]
    public void MalformedModeCannotBeCoercedIntoIgnoredEvidence(string field, object value)
    {
        var accumulator = new AudioServiceSnapshotAccumulator();
        Add(accumulator);
        var malformed = Values(streamId: 2);
        malformed[field] = value;
        AddValues(accumulator, malformed);
        var result = accumulator.Complete(Start, End);
        Assert.Empty(result.Streams);
        Assert.Equal([91], result.InvalidProcessIds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("91")]
    [InlineData(-1)]
    [InlineData(true)]
    public void UnknownSourceProcessInvalidatesGlobalSnapshot(object? value)
    {
        var accumulator = new AudioServiceSnapshotAccumulator();
        Add(accumulator);
        var malformed = Values();
        malformed["ProcessId"] = value;
        AddValues(accumulator, malformed);
        var result = accumulator.Complete(Start, End);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Streams);
    }

    [Fact]
    public void SystemSoundsPidZeroDoesNotInvalidateAnAppSnapshot()
    {
        var accumulator = new AudioServiceSnapshotAccumulator();
        Add(accumulator);
        Add(accumulator, processId: 0, streamId: 2);
        var result = accumulator.Complete(Start, End);
        Assert.Null(result.Error);
        Assert.Empty(result.InvalidProcessIds);
        Assert.Single(result.Streams);
    }

    [Fact]
    public void DuplicateIdenticalStreamDoesNotAppearTwice()
    {
        var accumulator = new AudioServiceSnapshotAccumulator();
        Add(accumulator, at: Start.AddMilliseconds(10));
        Add(accumulator, at: Start.AddMilliseconds(20));
        var result = accumulator.Complete(Start, End);
        Assert.Single(result.Streams);
        Assert.Empty(result.InvalidProcessIds);
    }

    [Fact]
    public void ChangingStreamDuringCaptureInvalidatesItsProcess()
    {
        var accumulator = new AudioServiceSnapshotAccumulator();
        Add(accumulator, rate: 48000);
        Add(accumulator, rate: 96000);
        var result = accumulator.Complete(Start, End);
        Assert.Empty(result.Streams);
        Assert.Equal([91], result.InvalidProcessIds);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ReusedServiceStreamIdInvalidatesBothProcessOwners()
    {
        var accumulator = new AudioServiceSnapshotAccumulator();
        Add(accumulator, processId: 91);
        Add(accumulator, processId: 92);
        var result = accumulator.Complete(Start, End);
        Assert.Empty(result.Streams);
        Assert.Equal([91, 92], result.InvalidProcessIds.Order());
    }

    [Fact]
    public void SameNumericStreamIdFromDifferentServiceInstancesIsNotMerged()
    {
        var accumulator = new AudioServiceSnapshotAccumulator();
        Add(accumulator, emitterPid: 42);
        Add(accumulator, emitterPid: 43);
        var result = accumulator.Complete(Start, End);
        Assert.Equal(2, result.Streams.Count);
        Assert.Empty(result.InvalidProcessIds);
    }

    [Fact]
    public void CapacityOverflowFailsClosedInsteadOfHidingAConflictingStream()
    {
        var accumulator = new AudioServiceSnapshotAccumulator();
        for (uint id = 1; id <= AudioServiceSnapshotAccumulator.MaximumStreams; id++) Add(accumulator, streamId: id);
        Assert.Equal(AudioServiceSnapshotAccumulator.MaximumStreams, accumulator.Complete(Start, End).Streams.Count);
        Add(accumulator, streamId: 99999, rate: 192000);
        var result = accumulator.Complete(Start, End);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Streams);
    }

    [Fact]
    public void DecodeFailureAndInvalidIntervalNeverPublishStreams()
    {
        var accumulator = new AudioServiceSnapshotAccumulator();
        Add(accumulator);
        accumulator.Fail("Unreadable payload.");
        Assert.Empty(accumulator.Complete(Start, End).Streams);
        Assert.Equal("Unreadable payload.", accumulator.Complete(Start, End).Error);
        var invalidInterval = new AudioServiceSnapshotAccumulator().Complete(End, Start);
        Assert.NotNull(invalidInterval.Error);
    }

    private static void Add(AudioServiceSnapshotAccumulator accumulator, int processId = 91, uint streamId = 1,
        int rate = 48000, DateTimeOffset? at = null, int emitterPid = 42)
        => AddValues(accumulator, Values(processId, streamId, rate), at, emitterPid);

    private static void AddValues(AudioServiceSnapshotAccumulator accumulator, Dictionary<string, object?> values,
        DateTimeOffset? at = null, int emitterPid = 42)
        => accumulator.Add(AudioServiceStreamEventParser.ProviderId, "AudioSrvStreamState", emitterPid, at ?? Start.AddMilliseconds(10), values);

    private static Dictionary<string, object?> Values(int pid = 91, uint streamId = 1, int rate = 48000) => new()
    {
        ["ProcessId"] = pid,
        ["StreamId"] = streamId,
        ["SessionInstanceId"] = "endpoint|player.exe|" + pid,
        ["DeviceId"] = "endpoint",
        ["AudioClientShareMode"] = 0,
        ["ShareMode"] = 0,
        ["EndpointConnectorType"] = 0,
        ["StreamFlags"] = -2013265918,
        ["FormatType"] = "WAVEFORMATEX",
        ["SampleRate"] = rate,
        ["NumberOfChannels"] = 2,
        ["ValidBitsPerSample"] = 32,
        ["SubFormat"] = "00000003-0000-0010-8000-00aa00389b71",
        ["IsRunning"] = true
    };
}

using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Core.Tests;

public sealed class BrowserEvidenceGuardTests
{
    [Fact]
    public void ReceiverDerivesExecutableIdentityAndUsesItsOwnMonotonicClock()
    {
        var clock = new ManualTime();
        var guard = Guard(clock);
        guard.BeginRequest(BrowserWireProtocolTests.Request);
        clock.Advance(TimeSpan.FromMilliseconds(123));
        var snapshot = guard.Accept(Message());
        Assert.Equal(@"C:\PROGRAM FILES\ZEN BROWSER\ZEN.EXE", snapshot.BrowserExecutablePath);
        Assert.Equal(123, snapshot.LastReceivedTimestamp);
        Assert.Equal(42, snapshot.BrowserProcessId);
        Assert.Equal(BrowserDecoderState.Available, snapshot.State);
        Assert.Equal(96000, snapshot.DecoderRateHz);
        Assert.Equal(48000, snapshot.OutputRateHz);
        Assert.True(snapshot.IsTabPinned);
    }

    [Fact]
    public void OnlyOneOutstandingReceiverRequestIsAllowedAndEachResponseIsConsumedOnce()
    {
        var guard = Guard();
        guard.BeginRequest(BrowserWireProtocolTests.Request);
        Assert.Throws<InvalidOperationException>(() => guard.BeginRequest(new string('c', 32)));
        guard.Accept(Message());
        Assert.Throws<InvalidDataException>(() => guard.Accept(Message()));
    }

    [Fact]
    public void IdenticalHeartbeatsRefreshFreshnessWithoutInvalidatingAnInFlightRateDecision()
    {
        var clock = new ManualTime();
        var guard = Guard(clock);
        var first = Accept(guard, Message());
        clock.Advance(TimeSpan.FromMilliseconds(1500));
        var heartbeat = Accept(guard, Message() with { Sequence = 2, OutputRateHz = 44100, Title = "Display-only update" });
        Assert.Equal(first.Version, heartbeat.Version);
        Assert.True(heartbeat.LastReceivedTimestamp > first.LastReceivedTimestamp);
        clock.Advance(TimeSpan.FromMilliseconds(1500));
        Assert.Equal(BrowserDecoderState.Available, guard.Snapshot.State);
        Assert.Equal(first.Version, guard.Snapshot.Version);
        var changed = Accept(guard, Message() with { Sequence = 3, DecoderRateHz = 192000 });
        Assert.True(changed.Version > heartbeat.Version);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OldConnectionAndRequestNoncesCannotSupplyEvidence(bool wrongConnection)
    {
        var guard = Guard();
        guard.BeginRequest(BrowserWireProtocolTests.Request);
        var message = Message();
        message = wrongConnection ? message with { ConnectionNonce = new string('c', 32) }
            : message with { RequestId = new string('c', 32) };
        Assert.Throws<InvalidDataException>(() => guard.Accept(message));
        Assert.Null(guard.Snapshot.DecoderRateHz);
    }

    [Fact]
    public void RepeatedSequencesAndChangedBackgroundLifetimesAreRejected()
    {
        var guard = Guard();
        guard.BeginRequest(BrowserWireProtocolTests.Request);
        guard.Accept(Message());
        guard.BeginRequest(BrowserWireProtocolTests.Request);
        Assert.Throws<InvalidDataException>(() => guard.Accept(Message()));
        guard.BeginRequest(BrowserWireProtocolTests.Request);
        Assert.Throws<InvalidDataException>(() => guard.Accept(Message() with { Sequence = 2, BrowserInstanceNonce = "restarted" }));
    }

    [Fact]
    public void LateRequestsAndExpiredEvidenceCannotBecomeFreshAgain()
    {
        var clock = new ManualTime();
        var guard = Guard(clock);
        guard.BeginRequest(BrowserWireProtocolTests.Request);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Throws<InvalidDataException>(() => guard.Accept(Message()));

        guard.BeginRequest(BrowserWireProtocolTests.Request);
        var accepted = guard.Accept(Message());
        clock.Advance(TimeSpan.FromSeconds(2));
        var expired = guard.Snapshot;
        Assert.Equal(BrowserDecoderState.TimedOut, expired.State);
        Assert.Null(expired.DecoderRateHz);
        Assert.True(expired.IsTabPinned);
        Assert.True(expired.Version > accepted.Version);
        Assert.Equal(expired.Version, guard.Snapshot.Version);
    }

    [Fact]
    public void WallClockJumpsCannotRefreshEvidenceAndBackwardsMonotonicTimeFailsClosed()
    {
        var clock = new ManualTime();
        var guard = Guard(clock);
        guard.BeginRequest(BrowserWireProtocolTests.Request);
        guard.Accept(Message());
        clock.Utc = clock.Utc.AddYears(10);
        Assert.Equal(BrowserDecoderState.Available, guard.Snapshot.State);
        clock.Timestamp = -1;
        Assert.Equal(BrowserDecoderState.TimedOut, guard.Snapshot.State);
    }

    [Fact]
    public void MediaReplacementAndRetiredDocumentResultsAreRejected()
    {
        var guard = Guard();
        Accept(guard, Message() with { MediaEpoch = 10 });
        guard.BeginRequest(BrowserWireProtocolTests.Request);
        Assert.Throws<InvalidDataException>(() => guard.Accept(Message() with { Sequence = 2, MediaEpoch = 9 }));
        Accept(guard, Message() with { Sequence = 3, DocumentNonce = "pin:new-document", MediaEpoch = 0 });
        guard.BeginRequest(BrowserWireProtocolTests.Request);
        Assert.Throws<InvalidDataException>(() => guard.Accept(Message() with { Sequence = 4, MediaEpoch = 11 }));
        var reFollow = Accept(guard, Message() with { Sequence = 5, DocumentNonce = "new-pin:document", MediaEpoch = 0 });
        Assert.Equal(BrowserDecoderState.Available, reFollow.State);
    }

    [Theory]
    [InlineData(BrowserDecoderState.Paused)]
    [InlineData(BrowserDecoderState.Conflict)]
    [InlineData(BrowserDecoderState.Unavailable)]
    public void PauseConflictAndTruncationHoldRatherThanSupplyingANewRate(BrowserDecoderState state)
    {
        var guard = Guard();
        var message = Message() with
        {
            Paused = state == BrowserDecoderState.Paused,
            Conflict = state == BrowserDecoderState.Conflict,
            Truncated = state == BrowserDecoderState.Unavailable
        };
        var snapshot = Accept(guard, message);
        Assert.Equal(state, snapshot.State);
        Assert.Null(snapshot.DecoderRateHz);
        Assert.True(snapshot.IsTabPinned);
    }

    [Fact]
    public void NoReaderRateCannotBeSubstitutedWithTheBrowserOutputRate()
    {
        var snapshot = Accept(Guard(), Message() with { DecoderRateHz = null, OutputRateHz = 192000 });
        Assert.Equal(BrowserDecoderState.Unavailable, snapshot.State);
        Assert.Null(snapshot.DecoderRateHz);
        Assert.Equal(192000, snapshot.OutputRateHz);
    }

    [Fact]
    public void ProviderProvenanceRemainsVisibleAndChangesTheEvidenceVersion()
    {
        var guard = Guard();
        var gecko = Accept(guard, Message());
        var helium = Accept(guard, Message() with { Sequence = 2, EvidenceField = BrowserWireProtocol.ChromiumEvidenceField });
        Assert.Equal(BrowserWireProtocol.ChromiumEvidenceField, helium.EvidenceField);
        Assert.Contains("Chromium", helium.Detail);
        Assert.True(helium.Version > gecko.Version);
    }

    [Fact]
    public void UnsupportedCapabilityRetainsTabIdentityWithoutInventingADecoderRate()
    {
        var guard = Guard();
        var unsupported = Accept(guard, Message() with { DecoderSupported = false, DecoderRateHz = null,
            OutputRateHz = null, Paused = false, EvidenceField = BrowserWireProtocol.ChromiumEvidenceField });
        Assert.False(unsupported.DecoderSupported);
        Assert.True(unsupported.IsTabPinned);
        Assert.Equal(7, unsupported.TabId);
        Assert.Equal("pin:document", unsupported.DocumentNonce);
        Assert.Equal(BrowserDecoderState.Unavailable, unsupported.State);
        Assert.Null(unsupported.DecoderRateHz);
        Assert.Contains("Windows PCM", unsupported.Detail);
        var same = Accept(guard, Message() with { Sequence = 2, DecoderSupported = false, DecoderRateHz = null,
            OutputRateHz = null, Paused = true, EvidenceField = BrowserWireProtocol.ChromiumEvidenceField });
        Assert.Equal(BrowserDecoderState.Unavailable, same.State);
        Assert.False(same.DecoderSupported);
    }

    [Fact]
    public void CapabilityChangesInvalidateEvidenceEvenWhenNoRateIsAvailable()
    {
        var guard = Guard();
        var unsupported = Accept(guard, Message() with { DecoderSupported = false, DecoderRateHz = null });
        var supportedButUnknown = Accept(guard, Message() with { Sequence = 2, DecoderRateHz = null });
        Assert.True(supportedButUnknown.DecoderSupported);
        Assert.Equal(BrowserDecoderState.Unavailable, supportedButUnknown.State);
        Assert.True(supportedButUnknown.Version > unsupported.Version);
        Assert.Contains("holding", supportedButUnknown.Detail);
        var ready = Accept(guard, Message() with { Sequence = 3 });
        Assert.Equal(BrowserDecoderState.Available, ready.State);
        Assert.True(ready.Version > supportedButUnknown.Version);
    }

    [Fact]
    public void UnsupportedHeartbeatsDoNotInventEvidenceVersionsAndDisconnectPreservesCapability()
    {
        var clock = new ManualTime();
        var guard = Guard(clock);
        var first = Accept(guard, Message() with { DecoderSupported = false, DecoderRateHz = null });
        clock.Advance(TimeSpan.FromMilliseconds(250));
        var heartbeat = Accept(guard, Message() with { Sequence = 2, DecoderSupported = false, DecoderRateHz = null });
        Assert.Equal(first.Version, heartbeat.Version);
        Assert.True(heartbeat.LastReceivedTimestamp > first.LastReceivedTimestamp);
        var disconnected = guard.Invalidate(BrowserDecoderState.Disconnected, "Disconnected");
        Assert.False(disconnected.DecoderSupported);
        Assert.True(disconnected.IsTabPinned);
        Assert.True(disconnected.Version > heartbeat.Version);
    }

    [Fact]
    public void OnlyExplicitUnfollowReleasesThePinnedTabPolicy()
    {
        var guard = Guard();
        Accept(guard, Message());
        var closed = Accept(guard, Message() with { Sequence = 2, Kind = "clear", ClearReason = "tabClosed",
            Paused = true, DecoderRateHz = null, OutputRateHz = null });
        Assert.True(closed.IsTabPinned);
        Assert.Null(closed.DecoderRateHz);
        var disconnected = guard.Invalidate(BrowserDecoderState.Disconnected, "Disconnected");
        Assert.True(disconnected.IsTabPinned);
        var stopped = Accept(guard, Message() with { Sequence = 3, Kind = "clear", ClearReason = "unfollowed",
            Paused = true, DecoderRateHz = null, OutputRateHz = null, TabId = null, DocumentNonce = null });
        Assert.False(stopped.IsTabPinned);
        Assert.Equal(BrowserDecoderState.Unfollowed, stopped.State);
    }

    [Fact]
    public void TemporaryUnavailableCanRecoverTheSameDocumentButNavigationCannotReviveIt()
    {
        var guard = Guard();
        Accept(guard, Message());
        Accept(guard, Message() with { Sequence = 2, Kind = "clear", ClearReason = "unavailable",
            Paused = true, DecoderRateHz = null, OutputRateHz = null, DocumentNonce = null });
        Assert.Equal(BrowserDecoderState.Available, Accept(guard, Message() with { Sequence = 3 }).State);
        Accept(guard, Message() with { Sequence = 4, Kind = "clear", ClearReason = "navigation",
            Paused = true, DecoderRateHz = null, OutputRateHz = null, DocumentNonce = null });
        guard.BeginRequest(BrowserWireProtocolTests.Request);
        Assert.Throws<InvalidDataException>(() => guard.Accept(Message() with { Sequence = 5 }));
        Assert.Equal(BrowserDecoderState.Available,
            Accept(guard, Message() with { Sequence = 6, DocumentNonce = "pin:new-navigation" }).State);
    }

    [Fact]
    public void SourceRestartRequiresANewGuardAndReceiverNonce()
    {
        var old = Guard();
        Accept(old, Message());
        var restarted = new BrowserEvidenceGuard(new string('c', 32), @"C:\Program Files\Zen Browser\zen.exe", 77,
            DateTimeOffset.UtcNow, new ManualTime());
        restarted.BeginRequest(BrowserWireProtocolTests.Request);
        Assert.Throws<InvalidDataException>(() => restarted.Accept(Message()));
        Assert.Null(restarted.Snapshot.DecoderRateHz);
        Assert.Equal(77, restarted.Snapshot.BrowserProcessId);
    }

    private static BrowserEvidenceGuard Guard(ManualTime? clock = null) => new(BrowserWireProtocolTests.Connection,
        @"C:\Program Files\Zen Browser\zen.exe", 42, DateTimeOffset.Parse("2026-10-20T00:00:00Z"), clock ?? new ManualTime());
    private static BrowserDecoderMessage Message() => BrowserWireProtocol.ParseResponse(BrowserWireProtocolTests.Bytes());
    private static BrowserDecoderSnapshot Accept(BrowserEvidenceGuard guard, BrowserDecoderMessage message)
    {
        guard.BeginRequest(message.RequestId);
        return guard.Accept(message);
    }
    private sealed class ManualTime : TimeProvider
    {
        public long Timestamp { get; set; }
        public DateTimeOffset Utc { get; set; } = DateTimeOffset.Parse("2026-10-20T00:00:00Z");
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => Utc;
        public void Advance(TimeSpan elapsed) => Timestamp += (long)elapsed.TotalMilliseconds;
    }
}

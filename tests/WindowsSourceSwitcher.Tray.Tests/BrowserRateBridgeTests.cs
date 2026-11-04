using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using WindowsSourceSwitcher.BrowserBridge;
using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

/// <summary>Transport/lifecycle tests inject peer trust. Native process/security checks have separate tests.</summary>
public sealed class BrowserRateBridgeTests
{
    [Fact]
    public async Task DisabledBridgeDoesNotCreateAnyPipeAndDisposalDoesNotStartOne()
    {
        var created = 0;
        var bridge = new BrowserRateBridge(HostPath, () => { created++; throw new InvalidOperationException(); }, (_, _) => Peer);
        Assert.Equal(BrowserDecoderState.Disabled, bridge.Snapshot.State);
        Assert.False(bridge.IsEvidenceCurrent(bridge.Snapshot.Version));
        await bridge.DisposeAsync();
        Assert.Equal(0, created);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => bridge.StartAsync());
    }

    [Fact]
    public async Task WaitingConnectionCancelsPromptlyWithoutAClient()
    {
        var name = PipeName();
        await using var bridge = Bridge(name);
        await bridge.StartAsync();
        var watch = Stopwatch.StartNew();
        await bridge.StopAsync();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(BrowserDecoderState.Disabled, bridge.Snapshot.State);
        Assert.False(bridge.Snapshot.IsTabPinned);
    }

    [Fact]
    public async Task CurrentRequestSuppliesTrustedDecoderEvidenceAndUnchangedHeartbeatPreservesItsVersion()
    {
        var name = PipeName();
        await using var bridge = Bridge(name);
        var changes = new ConcurrentQueue<long>();
        bridge.Changed += () => changes.Enqueue(bridge.Snapshot.Version);
        await bridge.StartAsync();
        using var client = await Connect(name);
        var firstRequest = await Request(client);
        await Reply(client, firstRequest, 1);
        var first = await WaitFor(bridge, snapshot => snapshot.State == BrowserDecoderState.Available);
        Assert.Equal(@"C:\PROGRAM FILES\ZEN BROWSER\ZEN.EXE", first.BrowserExecutablePath);
        Assert.Equal(96000, first.DecoderRateHz);
        Assert.Equal(48000, first.OutputRateHz);
        Assert.True(bridge.IsEvidenceCurrent(first.Version));
        var count = changes.Count;
        var next = await Request(client);
        Assert.NotEqual(firstRequest.RequestId, next.RequestId);
        Assert.Equal(firstRequest.ConnectionNonce, next.ConnectionNonce);
        await Reply(client, next, 2);
        var heartbeat = await WaitFor(bridge, snapshot => snapshot.LastReceivedTimestamp > first.LastReceivedTimestamp);
        Assert.Equal(first.Version, heartbeat.Version);
        Assert.Equal(count, changes.Count);
        Assert.True(bridge.IsEvidenceCurrent(first.Version));
    }

    [Fact]
    public async Task PauseAndDisconnectHoldThePinnedContextWithoutFreshDecoderEvidence()
    {
        var name = PipeName();
        await using var bridge = Bridge(name);
        await bridge.StartAsync();
        using var client = await Connect(name);
        await Reply(client, await Request(client), 1);
        var playing = await WaitFor(bridge, snapshot => snapshot.State == BrowserDecoderState.Available);
        var pause = await Request(client);
        await Reply(client, pause, 2, paused: true);
        var paused = await WaitFor(bridge, snapshot => snapshot.State == BrowserDecoderState.Paused);
        Assert.True(paused.IsTabPinned);
        Assert.Null(paused.DecoderRateHz);
        Assert.False(bridge.IsEvidenceCurrent(playing.Version));
        client.Dispose();
        var disconnected = await WaitFor(bridge, snapshot => snapshot.State == BrowserDecoderState.Disconnected);
        Assert.True(disconnected.IsTabPinned);
        Assert.Equal(playing.BrowserExecutablePath, disconnected.BrowserExecutablePath);
        Assert.Null(disconnected.DecoderRateHz);
        await bridge.StopAsync();
        Assert.False(bridge.Snapshot.IsTabPinned);
    }

    [Fact]
    public async Task InvalidResponseClearsFreshEvidenceAndPreservesTheChosenTabPolicy()
    {
        var name = PipeName();
        await using var bridge = Bridge(name);
        await bridge.StartAsync();
        using var client = await Connect(name);
        await Reply(client, await Request(client), 1);
        var playing = await WaitFor(bridge, snapshot => snapshot.State == BrowserDecoderState.Available);
        var request = await Request(client);
        var json = Response(request, 2);
        json["processId"] = 1234;
        await BrowserWireProtocol.WriteFrameAsync(client, Encoding.UTF8.GetBytes(json.ToJsonString()));
        var rejected = await WaitFor(bridge, snapshot => snapshot.State == BrowserDecoderState.Unavailable);
        Assert.True(rejected.IsTabPinned);
        Assert.Null(rejected.DecoderRateHz);
        Assert.False(bridge.IsEvidenceCurrent(playing.Version));
    }

    [Fact]
    public async Task RejectedPeerCannotSendAnyTrustedObservation()
    {
        var name = PipeName();
        await using var bridge = new BrowserRateBridge(HostPath, () => Server(name),
            (_, _) => throw new UnauthorizedAccessException("Wrong executable"));
        await bridge.StartAsync();
        using var client = await Connect(name);
        var rejected = await WaitFor(bridge, snapshot => snapshot.State == BrowserDecoderState.Unavailable);
        Assert.Contains("Wrong executable", rejected.Detail);
        Assert.Null(rejected.BrowserExecutablePath);
        Assert.Null(rejected.DecoderRateHz);
        Assert.False(rejected.IsTabPinned);
    }

    [Fact]
    public async Task ConcurrentStartsShareOneOwnerAndShutdownInvalidatesPendingReads()
    {
        var name = PipeName();
        var creates = 0;
        var bridge = new BrowserRateBridge(HostPath, () => { Interlocked.Increment(ref creates); return Server(name); }, (_, _) => Peer);
        await Task.WhenAll(bridge.StartAsync(), bridge.StartAsync(), bridge.StartAsync());
        using var client = await Connect(name);
        _ = await Request(client);
        await bridge.DisposeAsync();
        Assert.Equal(1, creates);
        Assert.Equal(BrowserDecoderState.Disabled, bridge.Snapshot.State);
        Assert.False(bridge.IsEvidenceCurrent(bridge.Snapshot.Version));
    }

    [Fact]
    public async Task ReceiverDeadlineExpiresWithoutAResponseAndNeverFallsBackToAnOutputRate()
    {
        var name = PipeName();
        await using var bridge = Bridge(name);
        await bridge.StartAsync();
        using var client = await Connect(name);
        await Reply(client, await Request(client), 1);
        var first = await WaitFor(bridge, snapshot => snapshot.State == BrowserDecoderState.Available);
        _ = await Request(client);
        var expired = await WaitFor(bridge, snapshot => snapshot.State == BrowserDecoderState.TimedOut,
            TimeSpan.FromSeconds(4));
        Assert.True(expired.IsTabPinned);
        Assert.Null(expired.DecoderRateHz);
        Assert.Null(expired.OutputRateHz);
        Assert.False(bridge.IsEvidenceCurrent(first.Version));
    }

    [Fact]
    public async Task UnsupportedHeliumAdapterPublishesIdentityWithoutClaimingFreshDecoderEvidence()
    {
        var name = PipeName();
        var helium = new BrowserPeerIdentity(@"C:\Program Files\imput\Helium\Application\chrome.exe", 70,
            DateTimeOffset.Parse("2026-10-20T00:00:00Z"), 71);
        await using var bridge = new BrowserRateBridge(HostPath, () => Server(name), (_, _) => helium);
        await bridge.StartAsync();
        using var client = await Connect(name);
        var request = await Request(client);
        var json = Response(request, 1);
        json["decoderSupported"] = false;
        json["decoderRateHz"] = null;
        json["outputRateHz"] = null;
        json["evidenceField"] = BrowserWireProtocol.ChromiumEvidenceField;
        await BrowserWireProtocol.WriteFrameAsync(client, Encoding.UTF8.GetBytes(json.ToJsonString()));
        var unsupported = await WaitFor(bridge, snapshot => snapshot.IsTabPinned && !snapshot.DecoderSupported);
        Assert.Equal(helium.BrowserProcessId, unsupported.BrowserProcessId);
        Assert.Equal(helium.BrowserExecutablePath.ToUpperInvariant(), unsupported.BrowserExecutablePath);
        Assert.Equal(BrowserDecoderState.Unavailable, unsupported.State);
        Assert.Null(unsupported.DecoderRateHz);
        Assert.False(bridge.IsEvidenceCurrent(unsupported.Version));
        Assert.Contains("Windows PCM", unsupported.Detail);
        request = await Request(client);
        json["requestId"] = request.RequestId;
        json["sequence"] = 2;
        await BrowserWireProtocol.WriteFrameAsync(client, Encoding.UTF8.GetBytes(json.ToJsonString()));
        var heartbeat = await WaitFor(bridge, snapshot => snapshot.LastReceivedTimestamp > unsupported.LastReceivedTimestamp);
        Assert.Equal(unsupported.Version, heartbeat.Version);
        client.Dispose();
        var disconnected = await WaitFor(bridge, snapshot => snapshot.State == BrowserDecoderState.Disconnected);
        Assert.False(disconnected.DecoderSupported);
        Assert.True(disconnected.IsTabPinned);
    }

    [Fact]
    public async Task RestartRequiresANewConnectionNonceAndCannotReviveOldEvidence()
    {
        var name = PipeName();
        await using var bridge = Bridge(name);
        await bridge.StartAsync();
        using var original = await Connect(name);
        var oldRequest = await Request(original);
        await Reply(original, oldRequest, 1);
        var playing = await WaitFor(bridge, snapshot => snapshot.State == BrowserDecoderState.Available);
        await bridge.StopAsync();
        await bridge.StartAsync();
        using var restarted = await Connect(name);
        var newRequest = await Request(restarted);
        Assert.NotEqual(oldRequest.ConnectionNonce, newRequest.ConnectionNonce);
        Assert.False(bridge.Snapshot.IsTabPinned);
        Assert.Null(bridge.Snapshot.DecoderRateHz);
        await Reply(restarted, oldRequest, 2);
        var rejected = await WaitFor(bridge, snapshot => snapshot.State == BrowserDecoderState.Unavailable);
        Assert.Null(rejected.DecoderRateHz);
        Assert.False(bridge.IsEvidenceCurrent(playing.Version));
        Assert.False(rejected.IsTabPinned);
    }

    [Fact]
    public async Task ReconnectedHostHoldsPriorPinnedContextUntilExplicitUnfollow()
    {
        var name = PipeName();
        await using var bridge = Bridge(name);
        await bridge.StartAsync();
        using var original = await Connect(name);
        await Reply(original, await Request(original), 1);
        var playing = await WaitFor(bridge, snapshot => snapshot.State == BrowserDecoderState.Available);
        original.Dispose();
        await WaitFor(bridge, snapshot => snapshot.State == BrowserDecoderState.Disconnected);
        using var reconnected = await Connect(name);
        var request = await Request(reconnected);
        Assert.True(bridge.Snapshot.IsTabPinned);
        Assert.Equal(playing.DocumentNonce, bridge.Snapshot.DocumentNonce);
        Assert.Null(bridge.Snapshot.DecoderRateHz);
        var stopped = Response(request, 1);
        stopped["kind"] = "clear";
        stopped["clearReason"] = "unfollowed";
        stopped["tabId"] = null;
        stopped["documentNonce"] = null;
        stopped["paused"] = true;
        stopped["decoderRateHz"] = null;
        stopped["outputRateHz"] = null;
        await BrowserWireProtocol.WriteFrameAsync(reconnected, Encoding.UTF8.GetBytes(stopped.ToJsonString()));
        var released = await WaitFor(bridge, snapshot => snapshot.State == BrowserDecoderState.Unfollowed);
        Assert.False(released.IsTabPinned);
        Assert.Null(released.DecoderRateHz);
        Assert.True(released.Version > playing.Version);
    }

    private static string HostPath => Path.Combine(Path.GetTempPath(), "WindowsSourceSwitcher.BrowserHost.exe");
    private static BrowserPeerIdentity Peer => new(@"C:\Program Files\Zen Browser\zen.exe", 42,
        DateTimeOffset.Parse("2026-10-20T00:00:00Z"), 43);
    private static string PipeName() => "DX5BridgeTests." + Guid.NewGuid().ToString("N");
    private static BrowserRateBridge Bridge(string name) => new(HostPath, () => Server(name), (_, _) => Peer);
    private static NamedPipeServerStream Server(string name) => new(name, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    private static async Task<NamedPipeClientStream> Connect(string name)
    {
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            // Hosted Windows runners can delay the listener when tests are running in parallel.
            await client.ConnectAsync(10_000);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
    private static async Task<BrowserProbeRequest> Request(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var bytes = await BrowserWireProtocol.ReadFrameAsync(stream, timeout.Token);
        Assert.NotNull(bytes);
        return BrowserWireProtocol.ParseRequest(bytes!);
    }
    private static async Task Reply(Stream stream, BrowserProbeRequest request, long sequence, bool paused = false) =>
        await BrowserWireProtocol.WriteFrameAsync(stream, Encoding.UTF8.GetBytes(Response(request, sequence, paused).ToJsonString()));
    private static JsonObject Response(BrowserProbeRequest request, long sequence, bool paused = false) => new()
    {
        ["protocolVersion"] = 1, ["kind"] = "decoderSnapshot", ["connectionNonce"] = request.ConnectionNonce,
        ["requestId"] = request.RequestId, ["sequence"] = sequence, ["browserInstanceNonce"] = "browser",
        ["tabId"] = 1, ["documentNonce"] = "pin:document", ["mediaEpoch"] = 1, ["paused"] = paused,
        ["decoderRateHz"] = 96000, ["outputRateHz"] = 48000,
        ["evidenceField"] = BrowserWireProtocol.EvidenceField, ["conflict"] = false
    };
    private static async Task<BrowserDecoderSnapshot> WaitFor(BrowserRateBridge bridge,
        Func<BrowserDecoderSnapshot, bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(deadline) < (timeout ?? TimeSpan.FromSeconds(3)))
        {
            var snapshot = bridge.Snapshot;
            if (predicate(snapshot)) return snapshot;
            await Task.Delay(10);
        }
        throw new TimeoutException("Bridge state: " + bridge.Snapshot.State + ": " + bridge.Snapshot.Detail);
    }
}

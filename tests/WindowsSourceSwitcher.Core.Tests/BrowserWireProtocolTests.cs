using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Core.Tests;

public sealed class BrowserWireProtocolTests
{
    internal const string Connection = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string Request = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void RequestRoundTripCarriesOnlyReceiverIssuedNonces()
    {
        var request = new BrowserProbeRequest(Connection, Request);
        Assert.Equal(request, BrowserWireProtocol.ParseRequest(BrowserWireProtocol.CreateRequest(request)));
        Assert.Throws<ArgumentException>(() => BrowserWireProtocol.CreateRequest(new("short", Request)));
        Assert.Throws<InvalidDataException>(() => BrowserWireProtocol.ParseRequest(Encoding.UTF8.GetBytes("{\"protocolVersion\":1,\"kind\":\"command\"}")));
    }

    [Fact]
    public void DecoderAndOutputRatesRemainDistinct()
    {
        var parsed = BrowserWireProtocol.ParseResponse(Bytes());
        Assert.Equal(96000, parsed.DecoderRateHz);
        Assert.Equal(48000, parsed.OutputRateHz);
        Assert.Equal("decoderSnapshot", parsed.Kind);
        Assert.False(parsed.Paused);
        Assert.Equal("pin:document", parsed.DocumentNonce);
    }

    [Fact]
    public void HeliumTrackConfigurationKeepsItsExactProviderAndCannotClaimGeckoCoverage()
    {
        var value = Object();
        value["evidenceField"] = BrowserWireProtocol.ChromiumEvidenceField;
        value["coverage"] = "selected-cdp-target";
        var parsed = BrowserWireProtocol.ParseResponse(Encoding.UTF8.GetBytes(value.ToJsonString()));
        Assert.Equal(BrowserWireProtocol.ChromiumEvidenceField, parsed.EvidenceField);
        Assert.Equal("igialjjnjplkdmieelilccmcnkbbgchp", BrowserWireProtocol.ChromiumExtensionId);
        value["coverage"] = "selected-accessible-frames";
        Assert.Throws<InvalidDataException>(() => BrowserWireProtocol.ParseResponse(Encoding.UTF8.GetBytes(value.ToJsonString())));
    }

    [Fact]
    public void StreamHeaderHasItsOwnCoverageAndCannotClaimWindowsOutput()
    {
        var value = Object();
        value["evidenceField"] = BrowserWireProtocol.ChromiumStreamEvidenceField;
        value["coverage"] = "selected-stream-init";
        Assert.Throws<InvalidDataException>(() => BrowserWireProtocol.ParseResponse(Encoding.UTF8.GetBytes(value.ToJsonString())));
        value["outputRateHz"] = null;
        var parsed = BrowserWireProtocol.ParseResponse(Encoding.UTF8.GetBytes(value.ToJsonString()));
        Assert.Equal(BrowserWireProtocol.ChromiumStreamEvidenceField, parsed.EvidenceField);
        Assert.Equal(96000, parsed.DecoderRateHz);
        value["coverage"] = "selected-direct-media";
        Assert.Throws<InvalidDataException>(() => BrowserWireProtocol.ParseResponse(Encoding.UTF8.GetBytes(value.ToJsonString())));
    }

    [Fact]
    public void DecoderCapabilityDistinguishesUnsupportedAdapterFromTemporaryUnknownMedia()
    {
        var legacy = BrowserWireProtocol.ParseResponse(Bytes());
        Assert.True(legacy.DecoderSupported);
        var value = Object();
        value["decoderSupported"] = false;
        value["decoderRateHz"] = null;
        value["outputRateHz"] = null;
        value["evidenceField"] = BrowserWireProtocol.ChromiumEvidenceField;
        value["coverage"] = "selected-cdp-target";
        var unsupported = BrowserWireProtocol.ParseResponse(Encoding.UTF8.GetBytes(value.ToJsonString()));
        Assert.False(unsupported.DecoderSupported);
        Assert.Equal(7, unsupported.TabId);
        Assert.Null(unsupported.DecoderRateHz);
        value["decoderSupported"] = true;
        var temporarilyUnknown = BrowserWireProtocol.ParseResponse(Encoding.UTF8.GetBytes(value.ToJsonString()));
        Assert.True(temporarilyUnknown.DecoderSupported);
        Assert.Null(temporarilyUnknown.DecoderRateHz);
    }

    [Fact]
    public void UnsupportedDecoderCannotSupplyARateEvenIfItsOtherFieldsAreValid()
    {
        var value = Object();
        value["decoderSupported"] = false;
        Assert.Throws<InvalidDataException>(() => BrowserWireProtocol.ParseResponse(Encoding.UTF8.GetBytes(value.ToJsonString())));
    }

    [Theory]
    [InlineData("protocolVersion", "2")]
    [InlineData("protocolVersion", "\"1\"")]
    [InlineData("kind", "\"setRate\"")]
    [InlineData("connectionNonce", "\"old\"")]
    [InlineData("requestId", "\"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB\"")]
    [InlineData("sequence", "0")]
    [InlineData("sequence", "1.5")]
    [InlineData("mediaEpoch", "-1")]
    [InlineData("tabId", "-1")]
    [InlineData("tabId", "null")]
    [InlineData("documentNonce", "null")]
    [InlineData("browserInstanceNonce", "\"\"")]
    [InlineData("paused", "\"false\"")]
    [InlineData("decoderSupported", "\"false\"")]
    [InlineData("decoderSupported", "null")]
    [InlineData("decoderSupported", "0")]
    [InlineData("conflict", "null")]
    [InlineData("decoderRateHz", "7999")]
    [InlineData("decoderRateHz", "768001")]
    [InlineData("decoderRateHz", "96000.5")]
    [InlineData("outputRateHz", "9999999999999")]
    [InlineData("evidenceField", "\"output.sampleRate\"")]
    [InlineData("coverage", "\"all-tabs-verified\"")]
    [InlineData("clearReason", "\"unfollowed\"")]
    public void InvalidUnsupportedAndWronglyTypedEvidenceIsRejected(string field, string replacement)
    {
        var value = Object();
        value[field] = JsonNode.Parse(replacement);
        Assert.Throws<InvalidDataException>(() => BrowserWireProtocol.ParseResponse(Encoding.UTF8.GetBytes(value.ToJsonString())));
    }

    [Theory]
    [InlineData("browserExecutablePath")]
    [InlineData("processId")]
    [InlineData("endpointId")]
    [InlineData("command")]
    [InlineData("rawDecoderJson")]
    public void JsonCannotSupplyTrustedProcessIdentitiesOrCommands(string field)
    {
        var value = Object();
        value[field] = "untrusted";
        Assert.Throws<InvalidDataException>(() => BrowserWireProtocol.ParseResponse(Encoding.UTF8.GetBytes(value.ToJsonString())));
    }

    [Fact]
    public void DuplicateFieldNamesAndOversizedStringsAreRejected()
    {
        var json = Object().ToJsonString();
        var duplicate = json[..^1] + ",\"sequence\":2}";
        Assert.Throws<InvalidDataException>(() => BrowserWireProtocol.ParseResponse(Encoding.UTF8.GetBytes(duplicate)));
        duplicate = json[..^1] + ",\"Sequence\":2}";
        Assert.Throws<InvalidDataException>(() => BrowserWireProtocol.ParseResponse(Encoding.UTF8.GetBytes(duplicate)));
        foreach (var (key, length) in new[] { ("browserInstanceNonce", 129), ("documentNonce", 129), ("title", 161) })
        {
            var value = Object();
            value[key] = new string('x', length);
            Assert.Throws<InvalidDataException>(() => BrowserWireProtocol.ParseResponse(Encoding.UTF8.GetBytes(value.ToJsonString())));
        }
        var control = Object();
        control["title"] = "Media\nControl";
        Assert.Throws<InvalidDataException>(() => BrowserWireProtocol.ParseResponse(Encoding.UTF8.GetBytes(control.ToJsonString())));
    }

    [Fact]
    public void ClearIsAnExplicitInvalidationAndCannotContainARate()
    {
        var value = Object();
        value["kind"] = "clear";
        value["clearReason"] = "unfollowed";
        value["tabId"] = null;
        value["documentNonce"] = null;
        value["paused"] = true;
        value["decoderRateHz"] = null;
        value["outputRateHz"] = null;
        var parsed = BrowserWireProtocol.ParseResponse(Encoding.UTF8.GetBytes(value.ToJsonString()));
        Assert.Equal("unfollowed", parsed.ClearReason);
        value["decoderSupported"] = false;
        Assert.False(BrowserWireProtocol.ParseResponse(Encoding.UTF8.GetBytes(value.ToJsonString())).DecoderSupported);
        value["decoderRateHz"] = 96000;
        Assert.Throws<InvalidDataException>(() => BrowserWireProtocol.ParseResponse(Encoding.UTF8.GetBytes(value.ToJsonString())));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{invalid")]
    [InlineData("{\"nested\": [[[[[[[[[[[[1]]]]]]]]]]]]}")]
    public void MalformedAndDeepJsonIsBounded(string json) =>
        Assert.Throws<InvalidDataException>(() => BrowserWireProtocol.ParseResponse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public async Task FramesUseLittleEndianLengthsAndSupportFragmentedReads()
    {
        var payload = Bytes();
        using var stream = new MemoryStream();
        await BrowserWireProtocol.WriteFrameAsync(stream, payload);
        var framed = stream.ToArray();
        Assert.Equal(payload.Length, (int)BinaryPrimitives.ReadUInt32LittleEndian(framed));
        using var fragmented = new FragmentedStream(framed);
        Assert.Equal(payload, await BrowserWireProtocol.ReadFrameAsync(fragmented));
        Assert.Null(await BrowserWireProtocol.ReadFrameAsync(fragmented));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(65537u)]
    [InlineData(uint.MaxValue)]
    public async Task FrameLengthIsRejectedBeforePayloadAllocation(uint length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await BrowserWireProtocol.ReadFrameAsync(stream); });
        Assert.Equal(4, stream.Position);
    }

    [Theory]
    [InlineData(new byte[] { 1 })]
    [InlineData(new byte[] { 2, 0, 0 })]
    [InlineData(new byte[] { 2, 0, 0, 0, 1 })]
    public async Task TruncatedHeadersAndPayloadsCannotBeTreatedAsCompleteFrames(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await BrowserWireProtocol.ReadFrameAsync(stream); });
    }

    [Fact]
    public async Task FullLimitFramesRoundTripButOversizedWritesDoNotWriteAnyBytes()
    {
        var payload = Encoding.UTF8.GetBytes("{}" + new string(' ', BrowserWireProtocol.MaximumFrameBytes - 2));
        using var stream = new MemoryStream();
        await BrowserWireProtocol.WriteFrameAsync(stream, payload);
        stream.Position = 0;
        Assert.Equal(payload, await BrowserWireProtocol.ReadFrameAsync(stream));
        using var rejected = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await BrowserWireProtocol.WriteFrameAsync(rejected, new byte[65537]); });
        Assert.Equal(0, rejected.Length);
    }

    internal static byte[] Bytes() => Encoding.UTF8.GetBytes(Object().ToJsonString());
    internal static JsonObject Object() => new()
    {
        ["protocolVersion"] = 1, ["kind"] = "decoderSnapshot", ["connectionNonce"] = Connection,
        ["requestId"] = Request, ["sequence"] = 1, ["browserInstanceNonce"] = "browser-instance",
        ["tabId"] = 7, ["documentNonce"] = "pin:document", ["mediaEpoch"] = 1, ["paused"] = false,
        ["decoderRateHz"] = 96000, ["outputRateHz"] = 48000, ["evidenceField"] = BrowserWireProtocol.EvidenceField,
        ["conflict"] = false, ["coverage"] = "selected-accessible-frames", ["truncated"] = false
    };

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }
}

using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class WasapiStreamEventParserTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Client = Guid.Parse("6bca88b9-a21c-458d-8904-236f60803a42");
    private const string Endpoint = "{0.0.0.00000000}.{TOPPING}";

    [Theory]
    [InlineData(44_100)]
    [InlineData(48_000)]
    [InlineData(96_000)]
    public void LiveRenderInitializationSchemaReportsSubmittedPcmRate(int rate)
    {
        var values = Initialization();
        values["SampleRate"] = rate;
        var signal = Parse(values);
        Assert.NotNull(signal);
        Assert.Equal(StreamSignalKind.Initialize, signal.Kind);
        Assert.Equal(1234, signal.ProcessId);
        Assert.Equal(Client, signal.ClientId);
        Assert.Equal(Endpoint, signal.EndpointId);
        Assert.Equal(rate, signal.SampleRateHz);
        Assert.Equal(Timestamp, signal.TimestampUtc);
    }

    [Theory]
    [InlineData("AudioClientStart", 1)]
    [InlineData("AudioClientStop", 2)]
    public void StartAndStopUseClientIdentityWithoutInventingAFormat(string name, int kind)
    {
        var values = new Dictionary<string, object?> { ["AudioClientGuidId"] = Client, ["HResult"] = 0 };
        var signal = Parse(values, name);
        Assert.NotNull(signal);
        Assert.Equal((StreamSignalKind)kind, signal.Kind);
        Assert.Equal(Client, signal.ClientId);
        Assert.Null(signal.SampleRateHz);
        Assert.Null(signal.EndpointId);
    }

    [Fact]
    public void WrongProviderIsRejectedEvenWithValidFields()
    {
        Assert.Null(WasapiStreamEventParser.Parse(Guid.NewGuid(), "AudioClientInitialize", 1234, Timestamp, Initialization()));
    }

    [Theory]
    [InlineData("AudioClientGetMixFormat")]
    [InlineData("AudioClientGetDevicePeriod")]
    [InlineData("AudioClientIsFormatSupported")]
    public void FormatQueriesCannotBecomeSourceRateEvidence(string eventName) => Assert.Null(Parse(Initialization(), eventName));

    [Theory]
    [InlineData("AudioClientInitialize")]
    [InlineData("AudioClientStart")]
    [InlineData("AudioClientStop")]
    public void FailedCallsDoNotChangeClientState(string eventName)
    {
        var values = Initialization();
        values["HResult"] = unchecked((int)0x88890004);
        Assert.Null(Parse(values, eventName));
    }

    [Theory]
    [InlineData("ShareMode", 1)]
    [InlineData("StreamType", 1)]
    [InlineData("StreamFlags", 0x20000)]
    [InlineData("NumberOfChannels", 0)]
    [InlineData("NumberOfChannels", 33)]
    public void ExclusiveCaptureLoopbackAndMalformedChannelFormatsAreRejected(string field, int value)
    {
        var values = Initialization();
        values[field] = value;
        Assert.Null(Parse(values));
    }

    [Fact]
    public void ProcessLoopbackCaptureIsRejected()
    {
        var values = Initialization();
        values["IsProcessLoopbackStream"] = true;
        Assert.Null(Parse(values));
    }

    [Theory]
    [InlineData("SampleRate", 0L)]
    [InlineData("SampleRate", 7_999L)]
    [InlineData("SampleRate", 768_001L)]
    [InlineData("SampleRate", 4_294_967_296L)]
    [InlineData("SampleRate", -4_294_923_196L)]
    public void InvalidRatesIncludingWrappingLongValuesAreRejected(string field, long value)
    {
        var values = Initialization();
        values[field] = value;
        Assert.Null(Parse(values));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void InvalidClientGuidsAreRejected(string clientId)
    {
        var values = Initialization();
        values["AudioClientGuidId"] = clientId;
        Assert.Null(Parse(values));
    }

    [Fact]
    public void EncodedOrUnknownSubformatsAreRejected()
    {
        var values = Initialization();
        values["SubFormat"] = Guid.Parse("00000092-0000-0010-8000-00aa00389b71");
        Assert.Null(Parse(values));
    }

    [Theory]
    [InlineData("00000001-0000-0010-8000-00aa00389b71")]
    [InlineData("00000003-0000-0010-8000-00aa00389b71")]
    public void PcmAndIeeeFloatAreAccepted(string subformat)
    {
        var values = Initialization();
        values["SubFormat"] = subformat;
        Assert.NotNull(Parse(values));
    }

    [Theory]
    [InlineData("HResult")]
    [InlineData("AudioClientGuidId")]
    [InlineData("ShareMode")]
    [InlineData("StreamType")]
    [InlineData("StreamFlags")]
    [InlineData("IsProcessLoopbackStream")]
    [InlineData("SampleRate")]
    [InlineData("NumberOfChannels")]
    [InlineData("SubFormat")]
    [InlineData("EndpointId")]
    public void EveryRequiredInitializationFieldIsStrictlyRequired(string missingField)
    {
        var values = Initialization();
        values.Remove(missingField);
        Assert.Null(Parse(values));
    }

    [Fact]
    public void CoercedStringsAndBlankEndpointsDoNotPassSchemaChecks()
    {
        var values = Initialization();
        values["SampleRate"] = "44100";
        Assert.Null(Parse(values));
        values = Initialization();
        values["IsProcessLoopbackStream"] = 0;
        Assert.Null(Parse(values));
        values = Initialization();
        values["EndpointId"] = " ";
        Assert.Null(Parse(values));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidProcessIdsAreRejected(int pid) =>
        Assert.Null(WasapiStreamEventParser.Parse(WasapiStreamEventParser.ProviderId,
            "AudioClientInitialize", pid, Timestamp, Initialization()));

    private static StreamSignal? Parse(IReadOnlyDictionary<string, object?> values, string name = "AudioClientInitialize") =>
        WasapiStreamEventParser.Parse(WasapiStreamEventParser.ProviderId, name, 1234, Timestamp, values);

    private static Dictionary<string, object?> Initialization() => new()
    {
        ["HResult"] = 0,
        ["AudioClientGuidId"] = Client,
        ["ShareMode"] = (uint)0,
        ["StreamType"] = (uint)0,
        ["StreamFlags"] = (uint)0,
        ["IsProcessLoopbackStream"] = false,
        ["SampleRate"] = (uint)44_100,
        ["NumberOfChannels"] = (ushort)2,
        ["SubFormat"] = Guid.Parse("00000003-0000-0010-8000-00aa00389b71"),
        ["EndpointId"] = Endpoint
    };
}

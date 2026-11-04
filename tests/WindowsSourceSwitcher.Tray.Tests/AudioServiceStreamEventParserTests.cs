using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class AudioServiceStreamEventParserTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 20, 12, 55, 51, TimeSpan.Zero);
    private const string Endpoint = "{0.0.0.00000000}.{1db95ce0-1e8b-4e67-a4d4-cea61bc6cadf}";
    private const string Session = Endpoint + "|\\Device\\HarddiskVolume3\\Player.exe%b{00000000-0000-0000-0000-000000000000}|1%b9872";

    [Theory]
    [InlineData(44_100)]
    [InlineData(48_000)]
    [InlineData(96_000)]
    [InlineData(192_000)]
    public void VerifiedSchemaPreservesSourceAndServiceIdentitySeparately(int sampleRate)
    {
        var values = Snapshot();
        values["SampleRate"] = sampleRate;
        var state = Parse(values);
        Assert.NotNull(state);
        Assert.Equal(2428, state.EmitterProcessId);
        Assert.Equal(9872, state.SourceProcessId);
        Assert.Equal(8u, state.StreamId);
        Assert.Equal(Session, state.SessionInstanceId);
        Assert.Equal(Endpoint, state.EndpointId);
        Assert.Equal(sampleRate, state.SampleRateHz);
        Assert.Equal(2, state.NumberOfChannels);
        Assert.Equal(32, state.ValidBitsPerSample);
        Assert.Equal(Guid.Parse("00000003-0000-0010-8000-00aa00389b71"), state.SubFormat);
        Assert.Equal(unchecked((uint)-2013265918), state.StreamFlags);
        Assert.True(state.IsRunning);
        Assert.Equal(Timestamp, state.TimestampUtc);
    }

    [Fact]
    public void StoppedSnapshotIsPreservedForCallerToHandleWithoutInventingPlayback()
    {
        var values = Snapshot();
        values["IsRunning"] = false;
        Assert.False(Assert.IsType<AudioServiceStreamState>(Parse(values)).IsRunning);
    }

    [Theory]
    [InlineData("ProcessId")]
    [InlineData("StreamId")]
    [InlineData("SessionInstanceId")]
    [InlineData("DeviceId")]
    [InlineData("AudioClientShareMode")]
    [InlineData("ShareMode")]
    [InlineData("EndpointConnectorType")]
    [InlineData("StreamFlags")]
    [InlineData("FormatType")]
    [InlineData("NumberOfChannels")]
    [InlineData("SampleRate")]
    [InlineData("SubFormat")]
    [InlineData("ValidBitsPerSample")]
    [InlineData("IsRunning")]
    public void EveryEvidenceFieldIsRequired(string name)
    {
        var values = Snapshot();
        values.Remove(name);
        Assert.Null(Parse(values));
    }

    [Theory]
    [InlineData("ProcessId", 0L)]
    [InlineData("ProcessId", -1L)]
    [InlineData("ProcessId", 2147483648L)]
    [InlineData("StreamId", 0L)]
    [InlineData("StreamId", -1L)]
    [InlineData("StreamId", 4294967296L)]
    [InlineData("AudioClientShareMode", 1L)]
    [InlineData("ShareMode", 1L)]
    [InlineData("EndpointConnectorType", 1L)]
    [InlineData("NumberOfChannels", 0L)]
    [InlineData("NumberOfChannels", 33L)]
    [InlineData("SampleRate", 7999L)]
    [InlineData("SampleRate", 768001L)]
    [InlineData("SampleRate", 4294967296L)]
    [InlineData("ValidBitsPerSample", 0L)]
    [InlineData("ValidBitsPerSample", 65L)]
    [InlineData("StreamFlags", -2147483649L)]
    [InlineData("StreamFlags", 4294967296L)]
    public void UnsupportedModesAndOutOfRangeIntegersFailClosed(string name, long value)
    {
        var values = Snapshot();
        values[name] = value;
        Assert.Null(Parse(values));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidEmitterProcessIsRejected(int emitter) => Assert.Null(AudioServiceStreamEventParser.Parse(
        AudioServiceStreamEventParser.ProviderId, "AudioSrvStreamState", emitter, Timestamp, Snapshot()));

    [Fact]
    public void WrongProviderAndRelatedButUnrecognizedEventsCannotSupplyRateEvidence()
    {
        Assert.Null(AudioServiceStreamEventParser.Parse(Guid.NewGuid(), "AudioSrvStreamState", 2428, Timestamp, Snapshot()));
        Assert.Null(Parse(Snapshot(), "AudioSrvStreamState_PolicyVolume"));
        Assert.Null(Parse(Snapshot(), "AudioClientInitialize"));
    }

    [Theory]
    [InlineData("FormatType", "WAVEFORMATEXTENSIBLE2")]
    [InlineData("FormatType", "waveformatex")]
    [InlineData("FormatType", "")]
    [InlineData("SessionInstanceId", " ")]
    [InlineData("DeviceId", "")]
    [InlineData("SubFormat", "not-a-guid")]
    [InlineData("SubFormat", "00000092-0000-0010-8000-00aa00389b71")]
    public void UnsupportedSchemaAndNonPcmDataAreRejected(string name, string value)
    {
        var values = Snapshot();
        values[name] = value;
        Assert.Null(Parse(values));
    }

    [Theory]
    [InlineData("00000001-0000-0010-8000-00aa00389b71")]
    [InlineData("00000003-0000-0010-8000-00aa00389b71")]
    public void PcmAndFloatSubformatsAcceptGuidOrGuidString(string format)
    {
        var values = Snapshot();
        values["SubFormat"] = format;
        Assert.NotNull(Parse(values));
        values["SubFormat"] = Guid.Parse(format);
        Assert.NotNull(Parse(values));
    }

    [Theory]
    [InlineData("WAVEFORMATEX", "00000001-0000-0010-8000-00aa00389b71", 48000, 16)]
    [InlineData("WAVEFORMATEX", "00000003-0000-0010-8000-00aa00389b71", 44100, 32)]
    [InlineData("WAVEFORMATEXTENSIBLE", "00000001-0000-0010-8000-00aa00389b71", 48000, 16)]
    [InlineData("WAVEFORMATEXTENSIBLE", "00000003-0000-0010-8000-00aa00389b71", 44100, 32)]
    public void VerifiedWaveFormatSchemasPreserveTheirSubmittedPcmFormat(string formatType,
        string subFormat, int rate, int bits)
    {
        var values = Snapshot();
        values["FormatType"] = formatType;
        values["SubFormat"] = subFormat;
        values["SampleRate"] = rate;
        values["ValidBitsPerSample"] = bits;
        var result = Assert.IsType<AudioServiceStreamState>(Parse(values));
        Assert.Equal(rate, result.SampleRateHz);
        Assert.Equal(bits, result.ValidBitsPerSample);
        Assert.Equal(Guid.Parse(subFormat), result.SubFormat);
    }

    [Theory]
    [InlineData("AudioClientShareMode", 1)]
    [InlineData("ShareMode", 1)]
    [InlineData("StreamFlags", 393218)]
    [InlineData("NumberOfChannels", 33)]
    [InlineData("ValidBitsPerSample", 65)]
    public void ExtensibleFormatDoesNotBypassModeLoopbackOrBoundsGuards(string field, int value)
    {
        var values = Snapshot();
        values["FormatType"] = "WAVEFORMATEXTENSIBLE";
        values[field] = value;
        Assert.Null(Parse(values));
    }

    [Fact]
    public void SignedAndUnsignedDwordFlagsProduceSameBitsAndRejectLoopbackInBothForms()
    {
        var values = Snapshot();
        var signed = Assert.IsType<AudioServiceStreamState>(Parse(values));
        values["StreamFlags"] = unchecked((uint)-2013265918);
        Assert.Equal(signed, Parse(values));
        values["StreamFlags"] = unchecked((int)(0x80000000u | 0x20000u));
        Assert.Null(Parse(values));
        values["StreamFlags"] = 0x80000000u | 0x20000u;
        Assert.Null(Parse(values));
    }

    [Fact]
    public void IntegerAndBooleanStringsOrFloatsCannotBeCoercedIntoEvidence()
    {
        var malformed = new (string Name, object Value)[]
        {
            ("SampleRate", "192000"), ("SampleRate", 192000.0), ("SampleRate", double.NaN),
            ("IsRunning", 1), ("IsRunning", "true"), ("ProcessId", true),
            ("StreamId", ulong.MaxValue), ("StreamFlags", ulong.MaxValue)
        };
        foreach (var (name, value) in malformed)
        {
            var values = Snapshot();
            values[name] = value;
            Assert.Null(Parse(values));
        }
    }

    [Theory]
    [InlineData(8000, 1, 1)]
    [InlineData(768000, 32, 64)]
    public void ExplicitSupportedNumericBoundsAreAccepted(int rate, int channels, int bits)
    {
        var values = Snapshot();
        values["SampleRate"] = rate;
        values["NumberOfChannels"] = channels;
        values["ValidBitsPerSample"] = bits;
        values["StreamId"] = uint.MaxValue;
        values["ProcessId"] = int.MaxValue;
        Assert.NotNull(Parse(values));
    }

    private static AudioServiceStreamState? Parse(IReadOnlyDictionary<string, object?> values,
        string name = "AudioSrvStreamState") => AudioServiceStreamEventParser.Parse(
            AudioServiceStreamEventParser.ProviderId, name, 2428, Timestamp, values);

    // Required fields from the preexisting 192 kHz stream snapshot in startup-trace.
    private static Dictionary<string, object?> Snapshot() => new()
    {
        ["StreamId"] = 8u,
        ["SessionInstanceId"] = Session,
        ["ProcessId"] = 9872,
        ["DeviceId"] = Endpoint,
        ["AudioClientShareMode"] = 0,
        ["EndpointConnectorType"] = 0,
        ["ShareMode"] = 0,
        ["StreamFlags"] = -2013265918,
        ["FormatType"] = "WAVEFORMATEX",
        ["NumberOfChannels"] = 2,
        ["SampleRate"] = 192000,
        ["SubFormat"] = "00000003-0000-0010-8000-00aa00389b71",
        ["ValidBitsPerSample"] = 32,
        ["IsRunning"] = true
    };
}

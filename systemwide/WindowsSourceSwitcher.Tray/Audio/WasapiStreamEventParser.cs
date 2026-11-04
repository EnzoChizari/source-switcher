using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.Tray.Audio;

internal enum StreamSignalKind { Initialize, Start, Stop }

internal sealed record StreamSignal(StreamSignalKind Kind, int ProcessId, Guid ClientId,
    DateTimeOffset TimestampUtc, string? EndpointId = null, int? SampleRateHz = null, int? PcmBitsPerSample = null);

/// <summary>Fail closed on unrecognized trace schemas. Field names were verified in a live Windows 11 capture.</summary>
internal static class WasapiStreamEventParser
{
    public static readonly Guid ProviderId = new("6e7b1892-5288-5fe5-8f34-e3b0dc671fd2");
    private static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid Float = new("00000003-0000-0010-8000-00aa00389b71");

    public static StreamSignal? Parse(Guid provider, string eventName, int processId,
        DateTimeOffset timestampUtc, IReadOnlyDictionary<string, object?> values)
    {
        if (provider != ProviderId || processId <= 0
            || !TryNumber(values, "HResult", out var hr) || hr != 0
            || !TryGuid(values, "AudioClientGuidId", out var clientId) || clientId == Guid.Empty)
            return null;
        if (eventName == "AudioClientStart")
            return new(StreamSignalKind.Start, processId, clientId, timestampUtc);
        if (eventName == "AudioClientStop")
            return new(StreamSignalKind.Stop, processId, clientId, timestampUtc);
        if (eventName != "AudioClientInitialize"
            || !TryNumber(values, "ShareMode", out var shareMode) || shareMode != 0
            || !TryNumber(values, "StreamType", out var streamType) || streamType != 0
            || !TryNumber(values, "StreamFlags", out var flags) || (flags & 0x20000) != 0
            || !values.TryGetValue("IsProcessLoopbackStream", out var loopback) || loopback is not false
            || !TryNumber(values, "SampleRate", out var rate) || rate is < SampleRates.MinimumHz or > SampleRates.MaximumHz
            || !TryNumber(values, "NumberOfChannels", out var channels) || channels is < 1 or > 32
            || !TryGuid(values, "SubFormat", out var format) || (format != Pcm && format != Float)
            || !values.TryGetValue("EndpointId", out var endpoint) || endpoint is not string endpointId
            || string.IsNullOrWhiteSpace(endpointId))
            return null;
        var bits = TryNumber(values, "ValidBitsPerSample", out var validBits)
            ? AudioServiceStreamEventParser.GetPcmWidth(format, validBits) : null;
        return new(StreamSignalKind.Initialize, processId, clientId, timestampUtc, endpointId, (int)rate, bits);
    }

    private static bool TryNumber(IReadOnlyDictionary<string, object?> values, string name, out long number)
    {
        number = 0;
        if (!values.TryGetValue(name, out var value)) return false;
        switch (value)
        {
            case int i: number = i; return true;
            case uint i: number = i; return true;
            case long i: number = i; return true;
            case ushort i: number = i; return true;
            case short i: number = i; return true;
            case byte i: number = i; return true;
            default: return false;
        }
    }

    private static bool TryGuid(IReadOnlyDictionary<string, object?> values, string name, out Guid guid)
    {
        guid = Guid.Empty;
        if (!values.TryGetValue(name, out var value)) return false;
        if (value is Guid id) { guid = id; return true; }
        return value is string text && Guid.TryParse(text, out guid);
    }
}

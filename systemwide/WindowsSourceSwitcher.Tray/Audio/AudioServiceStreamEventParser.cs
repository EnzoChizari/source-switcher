using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.Tray.Audio;

/// <summary>A service snapshot describes a stream; its ID is not an AudioClient GUID.</summary>
internal sealed record AudioServiceStreamState(
    int EmitterProcessId, int SourceProcessId, uint StreamId, string SessionInstanceId,
    string EndpointId, int SampleRateHz, int NumberOfChannels, int ValidBitsPerSample,
    Guid SubFormat, uint StreamFlags, bool IsRunning, DateTimeOffset TimestampUtc)
{
    public int? PcmBitsPerSample => AudioServiceStreamEventParser.GetPcmWidth(SubFormat, ValidBitsPerSample);
    public bool UsesFloatPcm => SubFormat == AudioServiceStreamEventParser.Float;
}

/// <summary>
/// Parses only the Audio Service stream-state schema observed in a live Windows 11
/// capture. Unknown schemas and unsupported stream modes provide no evidence.
/// The event-header PID belongs to AudioSrv; the payload PID owns the audio stream.
/// The caller must match the selected render endpoint; connector type zero is also
/// present on capture-endpoint snapshots and does not establish direction by itself.
/// </summary>
internal static class AudioServiceStreamEventParser
{
    public static readonly Guid ProviderId = new("3d2b6366-47a4-5e10-6974-e5f7de0d3f28");
    private static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");
    internal static readonly Guid Float = new("00000003-0000-0010-8000-00aa00389b71");

    // Float32 describes the stream's sample representation, not the original asset precision.
    internal static int? GetPcmWidth(Guid subFormat, long bits) =>
        (subFormat == Pcm && bits is 16 or 24 or 32) || (subFormat == Float && bits == 32) ? (int)bits : null;

    public static AudioServiceStreamState? Parse(Guid provider, string eventName,
        int emitterProcessId, DateTimeOffset timestampUtc, IReadOnlyDictionary<string, object?> values)
    {
        if (provider != ProviderId || eventName != "AudioSrvStreamState" || emitterProcessId <= 0
            || !TryInteger(values, "ProcessId", out var processId) || processId is < 1 or > int.MaxValue
            || !TryInteger(values, "StreamId", out var streamId) || streamId is < 1 or > uint.MaxValue
            || !TryText(values, "SessionInstanceId", out var sessionInstanceId)
            || !TryText(values, "DeviceId", out var endpointId)
            || !TryInteger(values, "AudioClientShareMode", out var clientShareMode) || clientShareMode != 0
            || !TryInteger(values, "ShareMode", out var shareMode) || shareMode != 0
            || !TryInteger(values, "EndpointConnectorType", out var connectorType) || connectorType != 0
            || !TryFlags(values, out var flags) || (flags & 0x20000u) != 0
            || !values.TryGetValue("FormatType", out var formatType)
            || formatType is not ("WAVEFORMATEX" or "WAVEFORMATEXTENSIBLE")
            || !TryInteger(values, "SampleRate", out var rate) || rate is < SampleRates.MinimumHz or > SampleRates.MaximumHz
            || !TryInteger(values, "NumberOfChannels", out var channels) || channels is < 1 or > 32
            || !TryInteger(values, "ValidBitsPerSample", out var bits) || bits is < 1 or > 64
            || !TryGuid(values, "SubFormat", out var subFormat) || (subFormat != Pcm && subFormat != Float)
            || !values.TryGetValue("IsRunning", out var runningValue) || runningValue is not bool isRunning)
            return null;

        return new(emitterProcessId, (int)processId, (uint)streamId, sessionInstanceId,
            endpointId, (int)rate, (int)channels, (int)bits, subFormat, flags, isRunning, timestampUtc);
    }

    private static bool TryFlags(IReadOnlyDictionary<string, object?> values, out uint flags)
    {
        flags = 0;
        if (!TryInteger(values, "StreamFlags", out var number) || number is < int.MinValue or > uint.MaxValue)
            return false;
        // TraceLogging projects DWORD flags as signed Int32 on the verified OS.
        flags = unchecked((uint)number);
        return true;
    }

    private static bool TryInteger(IReadOnlyDictionary<string, object?> values, string name, out long number)
    {
        number = 0;
        if (!values.TryGetValue(name, out var value)) return false;
        switch (value)
        {
            case byte n: number = n; return true;
            case sbyte n: number = n; return true;
            case short n: number = n; return true;
            case ushort n: number = n; return true;
            case int n: number = n; return true;
            case uint n: number = n; return true;
            case long n: number = n; return true;
            case ulong n when n <= long.MaxValue: number = (long)n; return true;
            default: return false;
        }
    }

    private static bool TryText(IReadOnlyDictionary<string, object?> values, string name, out string text)
    {
        text = "";
        if (!values.TryGetValue(name, out var value) || value is not string result || string.IsNullOrWhiteSpace(result))
            return false;
        text = result;
        return true;
    }

    private static bool TryGuid(IReadOnlyDictionary<string, object?> values, string name, out Guid guid)
    {
        guid = Guid.Empty;
        if (!values.TryGetValue(name, out var value)) return false;
        if (value is Guid result) { guid = result; return true; }
        return value is string text && Guid.TryParse(text, out guid);
    }
}

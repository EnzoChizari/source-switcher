using System.Buffers.Binary;
using System.Text.Json;

namespace WindowsSourceSwitcher.Core;

public sealed record BrowserProbeRequest(string ConnectionNonce, string RequestId);

public sealed record BrowserDecoderMessage(string Kind, string ConnectionNonce, string RequestId, long Sequence,
    string BrowserInstanceNonce, int? TabId, string? DocumentNonce, long MediaEpoch, bool Paused,
    int? DecoderRateHz, int? OutputRateHz, bool Conflict, string? Title, string? ClearReason, bool Truncated)
{
    public string EvidenceField { get; init; } = BrowserWireProtocol.EvidenceField;
    public bool DecoderSupported { get; init; } = true;
}

/// <summary>Bounded observation messages only. Process paths, commands and arbitrary payloads are never accepted.</summary>
public static class BrowserWireProtocol
{
    public const int MaximumFrameBytes = 64 * 1024;
    public const string ExtensionId = "dx5-native-rate-probe@local.invalid";
    public const string HostName = "local.dx5_source_switcher";
    public const string GeckoManifestFileName = HostName + ".json";
    public const string ChromiumManifestFileName = HostName + ".chromium.json";
    public const string ChromiumExtensionId = "igialjjnjplkdmieelilccmcnkbbgchp";
    public const string ChromiumOrigin = "chrome-extension://" + ChromiumExtensionId + "/";
    public const string EvidenceField = "decoder.reader.audioRate";
    public const string ChromiumEvidenceField = "cdp.Media.kAudioTracks.samples per second";
    public const string ChromiumHeaderEvidenceField = "direct-media.header.sampleRate";
    public const string ChromiumStreamEvidenceField = "stream-init.header.sampleRate";
    private static readonly HashSet<string> ResponseFields = new(StringComparer.Ordinal)
    {
        "protocolVersion", "kind", "connectionNonce", "requestId", "sequence", "browserInstanceNonce",
        "tabId", "documentNonce", "mediaEpoch", "paused", "decoderRateHz", "outputRateHz", "evidenceField",
        "conflict", "title", "clearReason", "coverage", "truncated", "decoderSupported"
    };

    public static byte[] CreateRequest(BrowserProbeRequest request)
    {
        if (!IsReceiverNonce(request.ConnectionNonce) || !IsReceiverNonce(request.RequestId))
            throw new ArgumentException("A browser request requires receiver-issued 128-bit hexadecimal nonces.");
        return JsonSerializer.SerializeToUtf8Bytes(new { protocolVersion = 1, kind = "request",
            connectionNonce = request.ConnectionNonce, requestId = request.RequestId });
    }

    public static BrowserProbeRequest ParseRequest(ReadOnlyMemory<byte> bytes)
    {
        using var document = ParseObject(bytes);
        var root = document.RootElement;
        RequireFields(root, ["protocolVersion", "kind", "connectionNonce", "requestId"]);
        if (Integer(root, "protocolVersion") != 1 || Text(root, "kind", 16) != "request")
            throw new InvalidDataException("Unsupported browser request.");
        var nonce = Text(root, "connectionNonce", 32);
        var requestId = Text(root, "requestId", 32);
        if (!IsReceiverNonce(nonce) || !IsReceiverNonce(requestId))
            throw new InvalidDataException("Invalid browser request nonce.");
        return new(nonce, requestId);
    }

    public static BrowserDecoderMessage ParseResponse(ReadOnlyMemory<byte> bytes)
    {
        using var document = ParseObject(bytes);
        var root = document.RootElement;
        RequireFields(root, ResponseFields);
        if (Integer(root, "protocolVersion") != 1)
            throw new InvalidDataException("Unsupported browser protocol version.");
        var kind = Text(root, "kind", 32);
        if (kind is not "decoderSnapshot" and not "clear") throw new InvalidDataException("Unsupported browser observation kind.");
        var connection = Text(root, "connectionNonce", 32);
        var request = Text(root, "requestId", 32);
        if (!IsReceiverNonce(connection) || !IsReceiverNonce(request)) throw new InvalidDataException("Invalid browser observation nonce.");
        var sequence = Integer(root, "sequence");
        var mediaEpoch = Integer(root, "mediaEpoch");
        if (sequence <= 0 || mediaEpoch < 0) throw new InvalidDataException("Browser sequence and media epoch are invalid.");
        var browserNonce = Text(root, "browserInstanceNonce", 128);
        var tabId = NullableInteger(root, "tabId");
        if (tabId is < 0) throw new InvalidDataException("Invalid browser tab ID.");
        var documentNonce = NullableText(root, "documentNonce", 128);
        var paused = Boolean(root, "paused");
        var decoderRate = NullableInteger(root, "decoderRateHz");
        var outputRate = NullableInteger(root, "outputRateHz");
        if (decoderRate is { } decoder && !SampleRates.IsValid(decoder)
            || outputRate is { } output && !SampleRates.IsValid(output))
            throw new InvalidDataException("Browser rates are outside the supported plausibility range.");
        var evidenceField = Text(root, "evidenceField", 64);
        if (evidenceField is not EvidenceField and not ChromiumEvidenceField and not ChromiumHeaderEvidenceField and not ChromiumStreamEvidenceField)
            throw new InvalidDataException("Unsupported browser decoder evidence field.");
        // A capability failure is different from a supported adapter temporarily lacking a
        // per-media decoder rate. Older Gecko messages omit the flag and remain supported.
        var decoderSupported = !root.TryGetProperty("decoderSupported", out _) || Boolean(root, "decoderSupported");
        if (!decoderSupported && decoderRate is not null)
            throw new InvalidDataException("An unsupported browser decoder adapter cannot supply a decoder rate.");
        var conflict = Boolean(root, "conflict");
        var title = OptionalText(root, "title", 160);
        var reason = OptionalText(root, "clearReason", 32);
        if ((evidenceField is ChromiumHeaderEvidenceField or ChromiumStreamEvidenceField) && (outputRate is not null || !decoderSupported))
            throw new InvalidDataException("A media header cannot supply an output rate or unsupported evidence.");
        var expectedCoverage = evidenceField switch
        {
            ChromiumHeaderEvidenceField => "selected-direct-media",
            ChromiumStreamEvidenceField => "selected-stream-init",
            ChromiumEvidenceField => "selected-cdp-target",
            _ => "selected-accessible-frames"
        };
        if (root.TryGetProperty("coverage", out _) && Text(root, "coverage", 64) != expectedCoverage)
            throw new InvalidDataException("Unsupported browser frame coverage.");
        var truncated = root.TryGetProperty("truncated", out _) && Boolean(root, "truncated");
        if (kind == "decoderSnapshot" && (tabId is null || documentNonce is null || reason is not null))
            throw new InvalidDataException("A decoder snapshot requires a pinned tab and document identity.");
        if (kind == "clear" && (reason is not "unfollowed" and not "tabClosed" and not "navigation" and not "unavailable"
            || !paused || decoderRate is not null || outputRate is not null || conflict))
            throw new InvalidDataException("An invalidation cannot supply a decoder or output rate.");
        return new(kind, connection, request, sequence, browserNonce, tabId, documentNonce, mediaEpoch,
            paused, decoderRate, outputRate, conflict, title, reason, truncated)
            { EvidenceField = evidenceField, DecoderSupported = decoderSupported };
    }

    public static async ValueTask<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0) return null;
        try { await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false); }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated browser frame header.", error); }
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > MaximumFrameBytes) throw new InvalidDataException("Browser frame exceeds the 64 KiB limit or is empty.");
        var payload = new byte[(int)length];
        try { await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false); }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated browser frame payload.", error); }
        return payload;
    }

    public static async ValueTask WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length is 0 or > MaximumFrameBytes) throw new InvalidDataException("Browser frame exceeds the 64 KiB limit or is empty.");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static JsonDocument ParseObject(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumFrameBytes) throw new InvalidDataException("Invalid browser payload size.");
        try
        {
            var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
            throw new InvalidDataException("A browser message must be an object.");
        }
        catch (JsonException error) { throw new InvalidDataException("Malformed browser JSON.", error); }
    }

    private static void RequireFields(JsonElement root, IEnumerable<string> allowed)
    {
        var fields = new HashSet<string>(allowed, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
            if (!fields.Contains(property.Name) || !seen.Add(property.Name))
                throw new InvalidDataException("Unknown or duplicate browser message field.");
    }

    private static long Integer(JsonElement root, string name) => root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer)
            ? integer : throw new InvalidDataException("Invalid or missing browser integer: " + name);
    private static int? NullableInteger(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Null) return null;
        var integer = Integer(root, name);
        return integer is >= int.MinValue and <= int.MaxValue ? (int)integer : throw new InvalidDataException("Browser integer is out of range: " + name);
    }
    private static bool Boolean(JsonElement root, string name) => root.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : throw new InvalidDataException("Invalid or missing browser flag: " + name);
    private static string Text(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Invalid or missing browser text: " + name);
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(char.IsControl))
            throw new InvalidDataException("Browser text is empty, oversized or contains control characters: " + name);
        return text;
    }
    private static string? NullableText(JsonElement root, string name, int maximum) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Null ? null : Text(root, name, maximum);
    private static string? OptionalText(JsonElement root, string name, int maximum) =>
        root.TryGetProperty(name, out _) ? NullableText(root, name, maximum) : null;
    private static bool IsReceiverNonce(string? value) => value is { Length: 32 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

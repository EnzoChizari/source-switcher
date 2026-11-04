namespace WindowsSourceSwitcher.Core;

public enum BrowserDecoderState
{
    Disabled, WaitingForHost, WaitingForProbe, Available, Paused, Conflict, Unavailable, Disconnected, TimedOut, Unfollowed
}

public sealed record BrowserDecoderSnapshot(long Version, string? BrowserExecutablePath, int BrowserProcessId,
    DateTimeOffset? BrowserStartedAtUtc, bool IsTabPinned, int? TabId, string? DocumentNonce, long MediaEpoch,
    bool Paused, int? DecoderRateHz, int? OutputRateHz, BrowserDecoderState State, string Detail,
    long LastReceivedTimestamp, string? BrowserInstanceNonce, string? Title)
{
    public string? EvidenceField { get; init; }
    public bool DecoderSupported { get; init; } = true;
    public int? FileBitsPerSample { get; init; }
    public static BrowserDecoderSnapshot Empty { get; } = new(0, null, 0, null, false, null, null, 0,
        true, null, null, BrowserDecoderState.Disabled, "Browser tab integration is off.", 0, null, null);

    /// <summary>Heartbeat timestamps and display-only output/title diagnostics do not invalidate a rate decision.</summary>
    public bool HasSameEvidenceAs(BrowserDecoderSnapshot other) =>
        StringComparer.OrdinalIgnoreCase.Equals(BrowserExecutablePath, other.BrowserExecutablePath)
        && BrowserProcessId == other.BrowserProcessId && BrowserStartedAtUtc == other.BrowserStartedAtUtc
        && IsTabPinned == other.IsTabPinned && TabId == other.TabId
        && string.Equals(DocumentNonce, other.DocumentNonce, StringComparison.Ordinal) && MediaEpoch == other.MediaEpoch
        && Paused == other.Paused && DecoderRateHz == other.DecoderRateHz && State == other.State
        && string.Equals(BrowserInstanceNonce, other.BrowserInstanceNonce, StringComparison.Ordinal)
        && string.Equals(EvidenceField, other.EvidenceField, StringComparison.Ordinal)
        && DecoderSupported == other.DecoderSupported && FileBitsPerSample == other.FileBitsPerSample;
}

/// <summary>Consumes receiver-issued requests once and stamps evidence with receiver monotonic time.</summary>
public sealed class BrowserEvidenceGuard
{
    public static readonly TimeSpan MaximumEvidenceAge = TimeSpan.FromSeconds(2);
    private readonly string _connectionNonce;
    private readonly TimeProvider _time;
    private readonly HashSet<string> _retiredDocuments = new(StringComparer.Ordinal);
    private readonly Queue<string> _retiredOrder = new();
    private BrowserDecoderSnapshot _snapshot;
    private string? _requestId, _browserNonce;
    private string? _currentDocument;
    private long _currentMediaEpoch;
    private long _requestSentAt, _lastSequence;

    public BrowserEvidenceGuard(string connectionNonce, string browserExecutablePath, int browserProcessId,
        DateTimeOffset browserStartedAtUtc, TimeProvider? timeProvider = null)
    {
        // Use the same nonce validation as the wire request parser.
        BrowserWireProtocol.CreateRequest(new(connectionNonce, connectionNonce));
        _connectionNonce = connectionNonce;
        _time = timeProvider ?? TimeProvider.System;
        _snapshot = BrowserDecoderSnapshot.Empty with { BrowserExecutablePath = SourceIdentity.FromExecutablePath(browserExecutablePath),
            BrowserProcessId = browserProcessId, BrowserStartedAtUtc = browserStartedAtUtc,
            State = BrowserDecoderState.WaitingForProbe, Detail = "Trusted browser host connected; waiting for a chosen tab." };
    }

    public BrowserDecoderSnapshot Snapshot
    {
        get
        {
            if (_snapshot.State == BrowserDecoderState.Available && !Fresh(_snapshot.LastReceivedTimestamp))
                Invalidate(BrowserDecoderState.TimedOut, "Tab decoder evidence expired; holding the confirmed target.");
            return _snapshot;
        }
    }

    public void BeginRequest(string requestId)
    {
        BrowserWireProtocol.CreateRequest(new(_connectionNonce, requestId));
        if (_requestId is not null) throw new InvalidOperationException("Only one browser probe may be outstanding.");
        _requestId = requestId;
        _requestSentAt = _time.GetTimestamp();
    }

    public BrowserDecoderSnapshot Accept(BrowserDecoderMessage message)
    {
        if (!string.Equals(message.ConnectionNonce, _connectionNonce, StringComparison.Ordinal)
            || _requestId is null || !string.Equals(message.RequestId, _requestId, StringComparison.Ordinal))
            throw new InvalidDataException("Browser response belongs to an old connection or request.");
        _requestId = null;
        if (!Fresh(_requestSentAt)) throw new InvalidDataException("Browser probe exceeded its receiver deadline.");
        if (message.Sequence <= _lastSequence) throw new InvalidDataException("Browser sequence was repeated or decreased.");
        if (_browserNonce is not null && !string.Equals(message.BrowserInstanceNonce, _browserNonce, StringComparison.Ordinal))
            throw new InvalidDataException("Browser background lifetime changed within a connection.");
        _browserNonce = message.BrowserInstanceNonce;
        _lastSequence = message.Sequence;
        var documentIdentity = message.TabId + ":" + message.DocumentNonce;
        if (message.DocumentNonce is not null && _retiredDocuments.Contains(documentIdentity))
            throw new InvalidDataException("Browser response belongs to a retired document selection.");
        if (message.DocumentNonce is not null && string.Equals(_currentDocument, documentIdentity, StringComparison.Ordinal)
            && message.MediaEpoch < _currentMediaEpoch)
            throw new InvalidDataException("Browser media epoch decreased.");
        if (message.Kind == "clear" && message.ClearReason is "navigation" or "tabClosed" or "unfollowed")
        {
            RetireCurrentDocument();
        }
        else if (message.DocumentNonce is not null)
        {
            if (!string.Equals(_currentDocument, documentIdentity, StringComparison.Ordinal)) RetireCurrentDocument();
            _currentDocument = documentIdentity;
            _currentMediaEpoch = message.MediaEpoch;
        }

        var state = message.Kind == "clear"
            ? message.ClearReason == "unfollowed" ? BrowserDecoderState.Unfollowed : BrowserDecoderState.Unavailable
            : !message.DecoderSupported ? BrowserDecoderState.Unavailable
            : message.Truncated ? BrowserDecoderState.Unavailable
            : message.Conflict ? BrowserDecoderState.Conflict
            : message.Paused ? BrowserDecoderState.Paused
            : message.DecoderRateHz is null ? BrowserDecoderState.Unavailable : BrowserDecoderState.Available;
        var detail = state switch
        {
            BrowserDecoderState.Available => message.EvidenceField switch
            {
                BrowserWireProtocol.ChromiumHeaderEvidenceField => "Chosen Chromium tab's media file header; browser processing and output rate are separate.",
                BrowserWireProtocol.ChromiumStreamEvidenceField => "Chosen Chromium tab's stream initialization header; browser processing and output rate are separate.",
                BrowserWireProtocol.ChromiumEvidenceField => "Chosen Chromium tab audio-track decoder configuration; browser processing and resampling may follow.",
                _ => "Chosen Gecko tab decoder reader rate; browser processing and resampling may follow."
            },
            BrowserDecoderState.Paused => "Chosen tab is paused; holding the confirmed target.",
            BrowserDecoderState.Conflict => "Chosen tab has conflicting or multiple unchosen audio sources; holding the confirmed target.",
            BrowserDecoderState.Unfollowed => "Tab following stopped explicitly.",
            _ when !message.DecoderSupported => "This browser does not expose decoder-rate evidence; tab identity is available and the application's Windows PCM rate may be used.",
            _ => "Chosen tab decoder is unavailable or changed; holding the confirmed target."
        };
        var next = _snapshot with { IsTabPinned = state != BrowserDecoderState.Unfollowed,
            TabId = message.TabId, DocumentNonce = message.DocumentNonce, MediaEpoch = message.MediaEpoch,
            Paused = message.Paused, DecoderRateHz = state == BrowserDecoderState.Available ? message.DecoderRateHz : null,
            FileBitsPerSample = state == BrowserDecoderState.Available ? message.FileBitsPerSample : null,
            OutputRateHz = message.OutputRateHz, State = state, Detail = detail,
            LastReceivedTimestamp = _time.GetTimestamp(), BrowserInstanceNonce = message.BrowserInstanceNonce,
            Title = message.Title, EvidenceField = message.EvidenceField, DecoderSupported = message.DecoderSupported };
        _snapshot = next with { Version = _snapshot.Version + (_snapshot.HasSameEvidenceAs(next) ? 0 : 1) };
        return _snapshot;
    }

    public BrowserDecoderSnapshot Invalidate(BrowserDecoderState state, string detail)
    {
        _requestId = null;
        var next = _snapshot with { DecoderRateHz = null, FileBitsPerSample = null,
            OutputRateHz = null, State = state, Detail = detail };
        _snapshot = next with { Version = _snapshot.Version + (_snapshot.HasSameEvidenceAs(next) ? 0 : 1) };
        return _snapshot;
    }

    private bool Fresh(long since)
    {
        var elapsed = _time.GetElapsedTime(since, _time.GetTimestamp());
        return elapsed >= TimeSpan.Zero && elapsed < MaximumEvidenceAge;
    }

    private void RetireCurrentDocument()
    {
        if (_currentDocument is { } retired && _retiredDocuments.Add(retired)) _retiredOrder.Enqueue(retired);
        while (_retiredOrder.Count > 64) _retiredDocuments.Remove(_retiredOrder.Dequeue());
        _currentDocument = null;
        _currentMediaEpoch = 0;
    }
}

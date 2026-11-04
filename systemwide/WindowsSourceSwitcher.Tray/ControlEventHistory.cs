namespace WindowsSourceSwitcher.Tray;

internal sealed record ControlHistoryEntry(long Sequence, DateTimeOffset Utc, string Kind, string Detail);

/// <summary>Bounded software observations, not a measurement of audible interruptions.</summary>
internal sealed class ControlEventHistory
{
    public const int DefaultCapacity = 200;
    private readonly int _capacity;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _gate = new();
    private readonly Queue<ControlHistoryEntry> _entries;
    private long _sequence;

    public ControlEventHistory(int capacity = DefaultCapacity, Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _entries = new Queue<ControlHistoryEntry>(capacity);
    }

    public IReadOnlyList<ControlHistoryEntry> Entries
    {
        get { lock (_gate) return _entries.Reverse().ToArray(); }
    }

    public ControlHistoryEntry Append(string kind, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        lock (_gate)
        {
            var entry = new ControlHistoryEntry(checked(++_sequence), _utcNow().ToUniversalTime(), kind, detail);
            if (_entries.Count == _capacity) _entries.Dequeue();
            _entries.Enqueue(entry);
            return entry;
        }
    }
}

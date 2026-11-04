namespace WindowsSourceSwitcher.Core;

/// <summary>Bounds disruptive endpoint-write attempts without delaying ordinary rate selection.</summary>
public sealed class FormatChangeThrottle
{
    private readonly Queue<TimeSpan> _attempts = new();
    private readonly int _maximum;
    private readonly TimeSpan _window;
    private TimeSpan _lastTime;

    public FormatChangeThrottle(int maximum = 6, TimeSpan? window = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        _window = window ?? TimeSpan.FromSeconds(10);
        if (_window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _maximum = maximum;
    }

    public TimeSpan RetryAfter(TimeSpan monotonicNow)
    {
        if (monotonicNow < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(monotonicNow));
        if (monotonicNow < _lastTime) _attempts.Clear();
        _lastTime = monotonicNow;
        while (_attempts.TryPeek(out var oldest) && monotonicNow - oldest >= _window) _attempts.Dequeue();
        return _attempts.Count < _maximum ? TimeSpan.Zero : _window - (monotonicNow - _attempts.Peek());
    }

    public void RecordAttempt(TimeSpan monotonicNow)
    {
        if (RetryAfter(monotonicNow) > TimeSpan.Zero)
            throw new InvalidOperationException("Endpoint write attempts are temporarily throttled.");
        _attempts.Enqueue(monotonicNow);
    }

    public void Reset() { _attempts.Clear(); _lastTime = TimeSpan.Zero; }
}

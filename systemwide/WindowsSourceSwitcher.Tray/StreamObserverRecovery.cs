namespace WindowsSourceSwitcher.Tray;

/// <summary>Retry decisions only; the existing refresh loop owns all monitor lifetimes.</summary>
internal sealed class StreamObserverRecovery
{
    private TimeSpan? _retryAt, _healthySince, _lastNow;
    private int _attempts;
    public string Status { get; private set; } = "Not started";

    public bool ShouldRetry(bool running, bool retryAllowed, TimeSpan now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(now, TimeSpan.Zero);
        if (_lastNow is { } previous && now < previous)
            _retryAt = _healthySince = null;
        _lastNow = now;
        if (running)
        {
            _retryAt = null;
            _healthySince ??= now;
            if (now - _healthySince.Value >= TimeSpan.FromSeconds(30)) _attempts = 0;
            Status = "Observer running";
            return false;
        }
        _healthySince = null;
        if (!retryAllowed)
        {
            _retryAt = null;
            Status = "Permission required; automatic retries paused. Use Refresh / retry after resolving access.";
            return false;
        }
        _retryAt ??= now + TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(_attempts, 5)));
        if (now < _retryAt)
        {
            Status = $"Observer stopped; retry in at least {Math.Ceiling((_retryAt.Value - now).TotalSeconds)} seconds at the next refresh.";
            return false;
        }
        _retryAt = null;
        _attempts = Math.Min(_attempts + 1, 5);
        Status = "Restarting the stream observer";
        return true;
    }

    public void Reset()
    {
        _retryAt = _healthySince = _lastNow = null;
        _attempts = 0;
        Status = "Not started";
    }
}

using System.Globalization;

namespace WindowsSourceSwitcher.Tray;

internal enum OutputConnectionState { None, Waiting, Available, Disconnected, Unknown }
internal sealed record OutputConnectionUpdate(OutputConnectionState State, string Detail);

/// <summary>Read-only state from a fresh endpoint census. It never chooses an output or changes settings.</summary>
internal sealed class PinnedOutputConnection
{
    private string? _endpoint;
    private bool _wasAvailable;
    private TimeSpan? _missingSince, _lastNow;
    public OutputConnectionState State { get; private set; }
    public string Detail { get; private set; } = "No output selected.";

    internal static bool? AvailabilityFromCensus(bool found, string? error) => found ? true : error is null ? false : null;

    public OutputConnectionUpdate? Observe(string? endpoint, bool? available, TimeSpan now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(now, TimeSpan.Zero);
        if (!StringComparer.OrdinalIgnoreCase.Equals(_endpoint, endpoint))
        {
            _endpoint = endpoint;
            _wasAvailable = false;
            _missingSince = null;
            State = OutputConnectionState.None;
        }
        if (_lastNow is { } last && now < last) _missingSince = null;
        _lastNow = now;
        if (endpoint is null)
            return Set(OutputConnectionState.None, "No output selected.");
        if (available is null)
            return Set(OutputConnectionState.Unknown, "Output connection unknown; endpoint read failed. Saved selection is retained.");
        if (!available.Value)
        {
            _missingSince ??= now;
            return _wasAvailable
                ? Set(OutputConnectionState.Disconnected, "Selected output disconnected. Waiting for the same device; master and target are retained.")
                : Set(OutputConnectionState.Waiting, "Waiting for the saved output. No other device will be selected automatically.");
        }
        var missing = _missingSince;
        _missingSince = null;
        _wasAvailable = true;
        if (missing is { } since && now >= since)
            return Set(OutputConnectionState.Available, "Selected output available again after "
                + (now - since).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)
                + " seconds. Fresh sessions and formats will be checked.");
        // Preserve a reconnect observation until the next transition, without repeated log entries.
        if (State == OutputConnectionState.Available) return null;
        return Set(OutputConnectionState.Available, "Selected output available.");
    }

    private OutputConnectionUpdate? Set(OutputConnectionState state, string detail)
    {
        if (State == state && Detail == detail) return null;
        State = state;
        Detail = detail;
        return new(state, detail);
    }
}

using System;
using System.Globalization;

namespace WindowsSourceSwitcher.Tray;

internal enum SessionContinuityState
{
    None,
    Waiting,
    Observed,
    Missing,
    Unknown,
    Cancelled
}

internal sealed record SessionContinuityUpdate(SessionContinuityState State, string Detail);

internal sealed class SessionContinuityTracker
{
    private string? _endpointId;
    private string? _sourceKey;
    private long _revision;
    private int _rate;
    private TimeSpan _beginTime;
    private TimeSpan _lastTime;

    public SessionContinuityState State { get; private set; } = SessionContinuityState.None;
    public string Detail { get; private set; } = "No recent format change.";

    public SessionContinuityUpdate Begin(string endpointId, string sourceKey, long revision, int rate, TimeSpan now)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            throw new ArgumentException("Endpoint ID cannot be null or whitespace.", nameof(endpointId));
        }
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            throw new ArgumentException("Source key cannot be null or whitespace.", nameof(sourceKey));
        }
        if (rate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), "Rate must be positive.");
        }
        if (now < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(now), "Monotonic time cannot be negative.");
        }

        _endpointId = endpointId;
        _sourceKey = sourceKey;
        _revision = revision;
        _rate = rate;
        _beginTime = now;
        _lastTime = now;

        State = SessionContinuityState.Waiting;
        Detail = $"Waiting for active Windows session after {FormatRate(rate)} format change.";
        return new SessionContinuityUpdate(State, Detail);
    }

    public SessionContinuityUpdate? Observe(
        string? endpointId,
        string? sourceKey,
        long revision,
        bool endpointAvailable,
        bool censusReliable,
        bool sourceActive,
        TimeSpan now)
    {
        if (State is SessionContinuityState.None or SessionContinuityState.Cancelled)
        {
            return null;
        }

        if (now < _lastTime)
        {
            return Transition(SessionContinuityState.Cancelled, "Session continuity tracking cancelled due to reversed monotonic time.");
        }
        _lastTime = now;

        if (!string.Equals(endpointId, _endpointId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(sourceKey, _sourceKey, StringComparison.OrdinalIgnoreCase) ||
            revision != _revision)
        {
            return Transition(SessionContinuityState.Cancelled, "Session continuity tracking cancelled due to context mismatch.");
        }

        if (!endpointAvailable)
        {
            return Transition(SessionContinuityState.Unknown, $"Endpoint unavailable after {FormatRate(_rate)} format change; session state unknown.");
        }

        if (censusReliable && sourceActive)
        {
            return Transition(SessionContinuityState.Observed, $"Active Windows session observed after the requested {FormatRate(_rate)} rate change; audibility unverified.");
        }

        if (State == SessionContinuityState.Observed)
        {
            return null;
        }

        if (!censusReliable)
        {
            if (now - _beginTime < TimeSpan.FromSeconds(3))
            {
                return null;
            }
            return Transition(SessionContinuityState.Unknown, $"Session census unreliable after {FormatRate(_rate)} format change; session state unknown.");
        }

        if (now - _beginTime < TimeSpan.FromSeconds(3))
        {
            return null;
        }

        return Transition(SessionContinuityState.Missing, $"No active Windows session observed after {FormatRate(_rate)} format change; the player may be paused, closed or restarting. Audibility is unverified.");
    }

    public SessionContinuityUpdate? Cancel()
    {
        if (State is SessionContinuityState.None or SessionContinuityState.Cancelled)
        {
            return null;
        }
        return Transition(SessionContinuityState.Cancelled, "Session continuity tracking cancelled.");
    }

    private SessionContinuityUpdate? Transition(SessionContinuityState newState, string newDetail)
    {
        if (State == newState && Detail == newDetail)
        {
            return null;
        }
        State = newState;
        Detail = newDetail;
        return new SessionContinuityUpdate(newState, newDetail);
    }

    private static string FormatRate(int rate)
    {
        double khz = rate / 1000.0;
        return rate % 1000 == 0
            ? $"{rate / 1000} kHz"
            : $"{khz.ToString("0.##", CultureInfo.InvariantCulture)} kHz";
    }
}

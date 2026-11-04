namespace WindowsSourceSwitcher.Core;

public sealed record StreamProcessIdentity(int ProcessId, DateTimeOffset StartedAtUtc);

public sealed record AudioStreamFormat(
    int ProcessId,
    DateTimeOffset ProcessStartedAtUtc,
    Guid ClientId,
    string EndpointId,
    int SampleRateHz,
    DateTimeOffset ObservedAtUtc,
    int? PcmBitsPerSample = null);

public sealed record StreamRateResult(int? SampleRateHz, string Detail, int? PcmBitsPerSample = null);

/// <summary>
/// Tracks observed render-client formats, not the format of the encoded media.
/// The caller must serialize access and clear the tracker if trace events are lost.
/// </summary>
public sealed class StreamRateTracker
{
    public const int MaximumStreams = 1024;

    private readonly Dictionary<(int ProcessId, Guid ClientId), TrackedStream> _streams = new();
    private bool _capacityExceeded;

    public int Count => _streams.Count;

    /// <summary>An initialized client is not playing until its start event is observed.</summary>
    public bool Initialize(AudioStreamFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format.ProcessId <= 0 || format.ClientId == Guid.Empty
            || string.IsNullOrWhiteSpace(format.EndpointId) || !SampleRates.IsValid(format.SampleRateHz)
            || format.ProcessStartedAtUtc == default || format.ObservedAtUtc < format.ProcessStartedAtUtc)
            return false;

        var key = (format.ProcessId, format.ClientId);
        if (_streams.TryGetValue(key, out var previous))
        {
            if (format.ObservedAtUtc < previous.LastEventUtc)
                return false;
        }
        else if (_streams.Count >= MaximumStreams)
        {
            var stopped = _streams.Where(pair => !pair.Value.IsPlaying)
                .OrderBy(pair => pair.Value.LastEventUtc).FirstOrDefault();
            if (stopped.Value is not null)
                _streams.Remove(stopped.Key);
            else
            {
                // Never drop a playing stream: that could hide a conflicting rate. Keep
                // the memory bound and withhold decisions until the trace owner resets it.
                _capacityExceeded = true;
                return false;
            }
        }

        _streams[key] = new(format, false, format.ObservedAtUtc);
        return true;
    }

    public bool SetPlaying(int processId, Guid clientId, bool isPlaying, DateTimeOffset eventUtc)
    {
        if (processId <= 0 || clientId == Guid.Empty
            || !_streams.TryGetValue((processId, clientId), out var stream)
            || eventUtc < stream.LastEventUtc)
            return false;

        _streams[(processId, clientId)] = stream with { IsPlaying = isPlaying, LastEventUtc = eventUtc };
        return true;
    }

    public StreamRateResult Resolve(string endpointId, IReadOnlyList<StreamProcessIdentity> currentProcesses)
    {
        ArgumentNullException.ThrowIfNull(currentProcesses);
        if (_capacityExceeded)
            return new(null, "Stream observation capacity exceeded; restart detection to restore complete evidence.");
        if (string.IsNullOrWhiteSpace(endpointId))
            return new(null, "No output endpoint selected.");

        var processIdentities = currentProcesses
            .Where(process => process.ProcessId > 0 && process.StartedAtUtc != default)
            .Select(process => (process.ProcessId, process.StartedAtUtc))
            .ToHashSet();
        var formats = _streams.Values
            .Where(stream => stream.IsPlaying
                && string.Equals(stream.Format.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase)
                && processIdentities.Contains((stream.Format.ProcessId, stream.Format.ProcessStartedAtUtc)))
            .Select(stream => (stream.Format.SampleRateHz, stream.Format.PcmBitsPerSample))
            .Distinct()
            .ToArray();
        var rates = formats.Select(format => format.SampleRateHz).Distinct().Take(2).ToArray();

        return rates.Length switch
        {
            0 => new(null, "No playing stream with an observed format for this source and output."),
            1 => new(rates[0], "Observed playing Windows audio-client stream rate.",
                formats.Length == 1 ? formats[0].PcmBitsPerSample : null),
            _ => new(null, "Playing streams use conflicting rates; retaining the current output rate.")
        };
    }

    public void Clear()
    {
        _streams.Clear();
        _capacityExceeded = false;
    }

    private sealed record TrackedStream(AudioStreamFormat Format, bool IsPlaying, DateTimeOffset LastEventUtc);
}

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using System.Diagnostics.Tracing;

namespace WindowsSourceSwitcher.Tray.Audio;

internal sealed record AudioServiceStreamSnapshotResult(
    IReadOnlyList<AudioServiceStreamState> Streams, IReadOnlyCollection<int> InvalidProcessIds,
    string? Error, bool WasObserved, DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc);

/// <summary>
/// Reads the service's existing-stream rundown without disturbing the persistent
/// Client observer. No samples are captured and no audio-client API is invoked.
/// Call on a worker thread: stopping a real-time trace can take several seconds.
/// </summary>
internal static class AudioServiceStreamSnapshot
{
    private static long _readySequence;

    public static AudioServiceStreamSnapshotResult Read()
    {
        var started = DateTimeOffset.UtcNow;
        var finished = started;
        var accumulator = new AudioServiceSnapshotAccumulator();
        TraceEventSession? session = null;
        ETWTraceEventSource? source = null;
        Task? reader = null;
        string? failure = null;
        long eventsLost = 0;
        var readySequence = Interlocked.Increment(ref _readySequence);
        var readerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            session = new TraceEventSession($"DX5SourceSwitcher.StreamSnapshot.{Environment.ProcessId}.{Guid.NewGuid():N}",
                TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate
                    | TraceEventSessionOptions.NoPerProcessorBuffering)
            {
                StopOnDispose = true,
                BufferSizeMB = 1,
                EnableProviderTimeoutMSec = 500
            };
            source = session.Source;
            source.Dynamic.All += e =>
            {
                if (e.ProviderGuid == SnapshotReadyEventSource.Log.Guid)
                {
                    if (e.EventName == nameof(SnapshotReadyEventSource.ReaderReady)
                        && e.PayloadByName("sequence") is long sequence && sequence == readySequence)
                        readerReady.TrySetResult();
                    return;
                }
                if (e.ProviderGuid != AudioServiceStreamEventParser.ProviderId || e.EventName != "AudioSrvStreamState") return;
                try
                {
                    accumulator.Add(e.ProviderGuid, e.EventName, e.ProcessID,
                        new DateTimeOffset(e.TimeStamp.ToUniversalTime()),
                        new TracePayloadValues(e.PayloadNames, e.PayloadValue));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
                {
                    accumulator.Fail("An audio-service snapshot event could not be decoded.");
                }
            };
            var ownedSource = source;
            // A new LongRunning task owns native Thread handles until a later GC even
            // after ProcessTrace finishes. This brief, deadline-bounded reader can use
            // a reusable pool worker; the readiness marker still proves it started.
            reader = Task.Run(() => ownedSource.Process());

            // A task-start flag does not prove the consumer entered ProcessTrace. On
            // a warm process a very short rundown can otherwise stop the ETW session
            // before ProcessTrace starts, which returns ERROR_INVALID_PARAMETER. One
            // owned metadata marker proves readiness, including with zero audio streams.
            // A single buffer avoids the real-time merge delay between per-CPU buffers.
            session.EnableProvider(SnapshotReadyEventSource.Log.Guid, TraceEventLevel.Informational, ulong.MaxValue);
            SnapshotReadyEventSource.Log.ReaderReady(readySequence);
            session.Flush();
            if (!readerReady.Task.Wait(TimeSpan.FromMilliseconds(500)))
                throw new TimeoutException("Existing-stream snapshot reader was not ready before its deadline.");

            // Enabling this provider emits its current stream state. Requesting an
            // additional CaptureState would combine two snapshots, retaining a stream
            // that disappeared between them. The synchronous timeout bounds the callback.
            started = DateTimeOffset.UtcNow;
            session.EnableProvider(AudioServiceStreamEventParser.ProviderId, TraceEventLevel.Informational, 0x4);
            finished = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            finished = DateTimeOffset.UtcNow;
            failure = "Existing-stream snapshot is unavailable: " + ex.Message;
        }
        finally
        {
            if (session is not null)
            {
                try
                {
                    // Source.EventsLost is the header's initial snapshot for real-time
                    // traces. Query the still-running controller for its current total.
                    eventsLost = session.EventsLost;
                    // Stop flushes the owned session. Await Process before disposing its
                    // source: CloseTrace/StopProcessing would cancel delivery instead of
                    // proving that every rundown event has reached the accumulator.
                    session.Stop(noThrow: false);
                    if (reader is not null)
                    {
                        if (!reader.Wait(TimeSpan.FromSeconds(5)))
                            failure ??= "Existing-stream snapshot did not drain before its deadline.";
                        else reader.GetAwaiter().GetResult();
                    }
                }
                catch (Exception ex)
                {
                    failure ??= "Existing-stream snapshot did not finish: " + ex.Message;
                }
                finally
                {
                    try { session.Dispose(); }
                    catch (Exception ex) { failure ??= "Existing-stream snapshot cleanup failed: " + ex.Message; }
                    if (reader is { IsCompleted: false })
                    {
                        // Cleanup after an unsuccessful stop is cancellation, never
                        // accepted as a complete snapshot. Dispose closes the trace.
                        try { source?.Dispose(); }
                        catch (Exception ex) { failure ??= "Existing-stream snapshot cleanup failed: " + ex.Message; }
                        try { reader.Wait(TimeSpan.FromSeconds(1)); } catch (Exception) { }
                    }
                }
            }
        }
        if (reader is { IsCompleted: false })
        {
            // An unsuccessfully cancelled reader owns the accumulator until it exits.
            // Never enumerate it concurrently or publish its unfinished observations.
            _ = reader.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return new([], [], failure ?? "Existing-stream snapshot reader did not stop.", false, started, finished);
        }
        return accumulator.Complete(started, finished, eventsLost, failure);
    }

    [EventSource(Name = "DX5SourceSwitcher-StreamSnapshot-Barrier")]
    private sealed class SnapshotReadyEventSource : EventSource
    {
        public static readonly SnapshotReadyEventSource Log = new();

        [Event(1, Level = EventLevel.Informational)]
        public void ReaderReady(long sequence) => WriteEvent(1, sequence);
    }
}

/// <summary>Bounded, callback-thread-only collection, published only after the reader drains.</summary>
internal sealed class AudioServiceSnapshotAccumulator
{
    public const int MaximumStreams = 1024;
    private readonly List<Entry> _entries = [];
    private string? _error;

    public void Add(Guid provider, string eventName, int emitterProcessId, DateTimeOffset timestampUtc,
        IReadOnlyDictionary<string, object?> values)
    {
        if (provider != AudioServiceStreamEventParser.ProviderId || eventName != "AudioSrvStreamState") return;
        if (_entries.Count >= MaximumStreams)
        {
            Fail("Existing-stream snapshot exceeded the observation limit.");
            return;
        }
        // Capture/exclusive/loopback streams are explicitly outside this controller.
        // A voice app can have a valid render stream alongside its microphone stream;
        // the latter must not invalidate the former just because their PID is shared.
        if (IsKnownNonCandidate(values))
        {
            _entries.Add(new(timestampUtc, null, null, true));
            return;
        }
        var state = AudioServiceStreamEventParser.Parse(provider, eventName, emitterProcessId, timestampUtc, values);
        var processId = ReadProcessId(values);
        _entries.Add(new(timestampUtc, state, processId, false));
    }

    public void Fail(string error) => _error ??= error;

    public AudioServiceStreamSnapshotResult Complete(DateTimeOffset started, DateTimeOffset completed,
        long eventsLost = 0, string? failure = null)
    {
        var entries = _entries.Where(entry => entry.TimestampUtc >= started && entry.TimestampUtc <= completed).ToArray();
        var error = failure ?? _error;
        if (completed < started) error ??= "Existing-stream snapshot had an invalid capture interval.";
        if (eventsLost != 0) error ??= "Audio trace events were lost during the existing-stream snapshot.";
        var invalid = new HashSet<int>();
        var streams = new Dictionary<(int EmitterProcessId, uint StreamId), AudioServiceStreamState>();
        foreach (var entry in entries)
        {
            if (entry.IsKnownNonCandidate) continue;
            if (entry.State is null)
            {
                if (entry.ProcessId is > 0) invalid.Add(entry.ProcessId.Value);
                // PID zero is the known system-sounds identity, not an app source.
                else if (entry.ProcessId is not 0) error ??= "An audio-service snapshot has no reliable source process ID.";
                continue;
            }
            var state = entry.State;
            var key = (state.EmitterProcessId, state.StreamId);
            if (streams.TryGetValue(key, out var previous)
                && previous with { TimestampUtc = default } != state with { TimestampUtc = default })
            {
                // A changing or reused service stream ID is not one stable snapshot.
                invalid.Add(previous.SourceProcessId);
                invalid.Add(state.SourceProcessId);
            }
            else streams[key] = state;
        }
        return new(error is null ? streams.Values.Where(stream => !invalid.Contains(stream.SourceProcessId)).ToArray() : [],
            invalid.ToArray(), error, entries.Length > 0, started, completed);
    }

    private static int? ReadProcessId(IReadOnlyDictionary<string, object?> values)
    {
        if (!values.TryGetValue("ProcessId", out var value)) return null;
        return value switch
        {
            int n when n >= 0 => n,
            uint n when n <= int.MaxValue => (int)n,
            long n when n is >= 0 and <= int.MaxValue => (int)n,
            ulong n when n <= int.MaxValue => (int)n,
            short n when n >= 0 => n,
            ushort n => n,
            byte n => n,
            sbyte n when n >= 0 => n,
            _ => null
        };
    }

    private static bool IsKnownNonCandidate(IReadOnlyDictionary<string, object?> values)
    {
        if (ReadInteger(values, "EndpointConnectorType") == 1
            || ReadInteger(values, "AudioClientShareMode") == 1 || ReadInteger(values, "ShareMode") == 1)
            return true;
        var flags = ReadInteger(values, "StreamFlags");
        return flags is >= int.MinValue and <= uint.MaxValue && (unchecked((uint)flags.Value) & 0x20000u) != 0;
    }

    private static long? ReadInteger(IReadOnlyDictionary<string, object?> values, string name)
    {
        if (!values.TryGetValue(name, out var value)) return null;
        return value switch
        {
            byte n => n, sbyte n => n, short n => n, ushort n => n,
            int n => n, uint n => n, long n => n, ulong n when n <= long.MaxValue => (long)n,
            _ => null
        };
    }

    private sealed record Entry(DateTimeOffset TimestampUtc, AudioServiceStreamState? State, int? ProcessId,
        bool IsKnownNonCandidate);
}

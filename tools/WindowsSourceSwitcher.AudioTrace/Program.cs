using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WindowsSourceSwitcher.AudioTrace;

internal static class Program
{
    // Deliberately stable and application-specific. NoRestartOnCreate prevents taking
    // over another diagnostic run or stopping any session we did not create.
    private const string SessionName = "DX5SourceSwitcher.AudioTrace.Diagnostics";
    private const int MaximumEvents = 25000;
    private static readonly (string Name, Guid Id)[] Providers =
    [
        ("Microsoft.Windows.Audio.Client", new("6e7b1892-5288-5fe5-8f34-e3b0dc671fd2"))
    ];

    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine("Usage: WindowsSourceSwitcher.AudioTrace.exe <new-output.jsonl> [seconds:1-120, default15] [--self-test] [--capture-state]");
            Console.WriteLine("Captures audio ETW metadata only. --self-test submits silent shared PCM streams at 44100,48000,96000 Hz; it never changes endpoint format.");
            return 0;
        }

        if (!TryParse(args, out var path, out var seconds, out var selfTest, out var captureState))
            return WriteFailure("invalid_arguments", "Expected a new output path, optional duration 1-120 seconds, and optional --self-test.", 64);

        try
        {
            using var output = new JsonLineWriter(path);
            return Capture(output, seconds, selfTest, captureState);
        }
        catch (Exception ex)
        {
            return WriteFailure("output_error", ex.Message, 74);
        }
    }

    private static bool TryParse(string[] args, out string path, out int seconds, out bool selfTest, out bool captureState)
    {
        path = string.Empty;
        seconds = 15;
        selfTest = false;
        captureState = false;
        if (args.Length is < 1 or > 4 || args[0].StartsWith("--", StringComparison.Ordinal)) return false;
        path = Path.GetFullPath(args[0]);
        var foundDuration = false;
        foreach (var arg in args.Skip(1))
        {
            if (arg == "--self-test" && !selfTest) selfTest = true;
            else if (arg == "--capture-state" && !captureState) captureState = true;
            else if (!foundDuration && int.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is >= 1 and <= 120)
            {
                seconds = parsed;
                foundDuration = true;
            }
            else return false;
        }
        return !selfTest || seconds >= 4;
    }

    private static int Capture(JsonLineWriter output, int seconds, bool selfTest, bool captureState)
    {
        using var cancellation = new CancellationTokenSource();
        TraceEventSession? session = null;
        Task? stopper = null;
        Task? probe = null;
        var count = 0;
        var capped = 0;
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += onCancel;
        output.Write(new { kind = "start", timestampUtc = DateTimeOffset.UtcNow, processId = Environment.ProcessId,
            osVersion = Environment.OSVersion.Version.ToString(), sessionName = SessionName, seconds, selfTest, captureState,
            description = "Audio ETW metadata only; no endpoint format writes and no audio capture.",
            providers = Providers.Select(p => new { p.Name, guid = p.Id }) });
        try
        {
            session = new TraceEventSession(SessionName, TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate)
            {
                StopOnDispose = true,
                BufferSizeMB = 8
            };
            var source = session.Source;
            source.Dynamic.All += e =>
            {
                if (!Providers.Any(p => p.Id == e.ProviderGuid)) return;
                if (Interlocked.Increment(ref count) > MaximumEvents)
                {
                    if (Interlocked.Exchange(ref capped, 1) == 0)
                    {
                        output.Write(new { kind = "event_limit", maximumEvents = MaximumEvents });
                        cancellation.Cancel();
                    }
                    return;
                }
                try
                {
                    var payload = new Dictionary<string, object?>();
                    foreach (var name in e.PayloadNames)
                    {
                        try { payload[name] = SafeValue(e.PayloadByName(name), 0); }
                        catch (Exception ex) { payload[name] = new { decodeError = ex.GetType().Name }; }
                    }
                    output.Write(new { kind = "event", timestampUtc = e.TimeStamp.ToUniversalTime(),
                        provider = e.ProviderName, providerGuid = e.ProviderGuid, eventName = e.EventName,
                        eventId = (int)e.ID, version = e.Version, processId = e.ProcessID, threadId = e.ThreadID,
                        activityId = e.ActivityID, payload });
                }
                catch (Exception ex)
                {
                    output.Write(new { kind = "decode_error", eventName = e.EventName, error = ex.Message });
                }
            };

            foreach (var provider in Providers)
                session.EnableProvider(provider.Id, TraceEventLevel.Verbose, ulong.MaxValue);

            if (captureState)
            {
                session.CaptureState(Providers[0].Id, ulong.MaxValue, 0, null);
                output.Write(new { kind = "capture_state_requested", provider = Providers[0].Name });
            }

            var ownedSession = session;
            stopper = Task.Run(async () =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(seconds), cancellation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                cancellation.Cancel();
                // Only this newly-created session is stopped. No kernel session or WPR session is touched.
                try { ownedSession.Stop(); }
                catch (Exception ex) { output.Write(new { kind = "stop_error", error = ex.Message }); }
            });

            if (selfTest)
                probe = Task.Run(() => RunSilentProbe(output, cancellation.Token));

            Console.WriteLine($"Listening for {seconds} seconds. Output: {output.Path}");
            source.Process();
            cancellation.Cancel();
            stopper.GetAwaiter().GetResult();
            probe?.GetAwaiter().GetResult();
            output.Write(new { kind = "complete", timestampUtc = DateTimeOffset.UtcNow,
                eventsWritten = Math.Min(count, MaximumEvents), eventLimitReached = capped != 0,
                eventsLost = source.EventsLost });
            return 0;
        }
        catch (Exception ex)
        {
            cancellation.Cancel();
            var denied = ex is UnauthorizedAccessException || ex.HResult == unchecked((int)0x80070005)
                || ex is Win32Exception { NativeErrorCode: 5 };
            var busy = ex.HResult == unchecked((int)0x800700B7) || ex is Win32Exception { NativeErrorCode: 183 };
            var code = denied ? 5 : busy ? 7 : 1;
            var category = denied ? "access_denied" : busy ? "session_already_exists" : "trace_error";
            output.Write(new { kind = "error", category, error = ex.Message, hresult = $"0x{ex.HResult:X8}",
                guidance = denied ? "Run this diagnostic from an elevated terminal or an account authorized for ETW. The tool does not elevate itself." : null });
            return WriteFailure(category, ex.Message, code);
        }
        finally
        {
            cancellation.Cancel();
            try { stopper?.GetAwaiter().GetResult(); } catch { }
            try { probe?.GetAwaiter().GetResult(); } catch { }
            session?.Dispose();
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static void RunSilentProbe(JsonLineWriter output, CancellationToken cancellation)
    {
        try
        {
            if (cancellation.WaitHandle.WaitOne(500)) return;
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            output.Write(new { kind = "self_test_endpoint", endpointId = device.ID, name = device.FriendlyName,
                before = ReadMixFormat(device), processId = Environment.ProcessId });
            foreach (var rate in new[] { 44100, 48000, 96000 })
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    using var client = device.AudioClient;
                    var format = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
                    var testSessionId = Guid.NewGuid();
                    output.Write(new { kind = "self_test_request", sampleRate = rate, channels = 2, bitsPerSample = 32,
                        shareMode = "Shared", streamFlags = "AutoConvertPcm|SrcDefaultQuality", testSessionId,
                        timestampUtc = DateTimeOffset.UtcNow, processId = Environment.ProcessId });
                    client.Initialize(AudioClientShareMode.Shared,
                        AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                        1_000_000, 0, format, testSessionId);
                    var render = client.AudioRenderClient;
                    var initialFrames = client.BufferSize;
                    render.GetBuffer(initialFrames);
                    render.ReleaseBuffer(initialFrames, AudioClientBufferFlags.Silent);
                    client.Start();
                    try
                    {
                        var watch = Stopwatch.StartNew();
                        while (watch.ElapsedMilliseconds < 550 && !cancellation.WaitHandle.WaitOne(20))
                        {
                            var available = client.BufferSize - client.CurrentPadding;
                            if (available <= 0) continue;
                            render.GetBuffer(available);
                            render.ReleaseBuffer(available, AudioClientBufferFlags.Silent);
                        }
                    }
                    finally { client.Stop(); }
                    output.Write(new { kind = "self_test_success", sampleRate = rate, testSessionId,
                        timestampUtc = DateTimeOffset.UtcNow });
                }
                catch (Exception ex)
                {
                    output.Write(new { kind = "self_test_failure", sampleRate = rate, error = ex.Message,
                        hresult = $"0x{ex.HResult:X8}" });
                }
            }
            output.Write(new { kind = "self_test_endpoint_after", endpointId = device.ID,
                after = ReadMixFormat(device), timestampUtc = DateTimeOffset.UtcNow });
        }
        catch (OperationCanceledException) { output.Write(new { kind = "self_test_cancelled" }); }
        catch (Exception ex) { output.Write(new { kind = "self_test_error", error = ex.Message }); }
    }

    private static object ReadMixFormat(MMDevice device)
    {
        using var client = device.AudioClient;
        var format = client.MixFormat;
        return new { format.SampleRate, format.Channels, format.BitsPerSample, encoding = format.Encoding.ToString() };
    }

    private static object? SafeValue(object? value, int depth)
    {
        if (value is null || value is string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal)
            return value is string text && text.Length > 4096 ? text[..4096] + " [truncated]" : value;
        if (value is float single) return float.IsFinite(single) ? single : single.ToString(CultureInfo.InvariantCulture);
        if (value is double number) return double.IsFinite(number) ? number : number.ToString(CultureInfo.InvariantCulture);
        if (value is Guid or DateTime or DateTimeOffset or Enum or IntPtr or UIntPtr) return value.ToString();
        if (value is byte[] bytes) return new { binaryLength = bytes.Length, binaryOmitted = true };
        if (depth >= 4) return "[maximum nesting reached]";
        if (value is IDictionary dictionary)
        {
            var result = new Dictionary<string, object?>();
            foreach (DictionaryEntry item in dictionary)
            {
                if (result.Count >= 128) break;
                result[item.Key?.ToString() ?? "null"] = SafeValue(item.Value, depth + 1);
            }
            return result;
        }
        if (value is IEnumerable sequence)
        {
            var result = new List<object?>();
            foreach (var item in sequence)
            {
                if (result.Count >= 128) { result.Add("[truncated]"); break; }
                result.Add(SafeValue(item, depth + 1));
            }
            return result;
        }
        var representation = value.ToString() ?? string.Empty;
        return representation.Length > 4096 ? representation[..4096] + " [truncated]" : representation;
    }

    private static int WriteFailure(string category, string message, int code)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { kind = "error", category, message, exitCode = code }));
        return code;
    }

    private sealed class JsonLineWriter : IDisposable
    {
        private readonly StreamWriter writer;
        private readonly object gate = new();
        internal string Path { get; }
        internal JsonLineWriter(string path)
        {
            Path = path;
            writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
            { AutoFlush = true };
        }
        internal void Write(object value)
        {
            var json = JsonSerializer.Serialize(value);
            lock (gate) writer.WriteLine(json);
        }
        public void Dispose() { lock (gate) writer.Dispose(); }
    }
}

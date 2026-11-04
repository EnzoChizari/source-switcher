using System.Diagnostics;
using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WindowsSourceSwitcher.Tray.Audio;

/// <summary>Explicit diagnostic mode; tests the production detector with silent streams, never the format setter.</summary>
internal static class StreamDetectionProbe
{
    internal sealed record ProbeObservation(int Round, int RequestedRateHz, int? DetectedRateHz, bool Passed,
        double? InitializationToDetectionMilliseconds, double? StartToDetectionMilliseconds,
        int SessionNotifications, int TraceFlushes, string? Detail);
    internal sealed record ProbeResult(string Mode, bool Passed, string Endpoint,
        int MixRateBeforeHz, int MixRateAfterHz, double StreamTestsMilliseconds,
        IReadOnlyList<ProbeObservation> Observations);

    public static ProbeResult Run()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var process = Process.GetCurrentProcess();
        var executable = process.MainModule?.FileName ?? throw new InvalidOperationException("Current executable path unavailable.");
        var monitor = new WasapiStreamRateMonitor();
        var before = ReadMixRate(device);
        var rows = new List<ProbeObservation>();
        var allPassed = true;
        using var evidenceChanged = new AutoResetEvent(false);
        using var watcher = new AudioSessionWatcher(device.ID);
        var flushTasks = new ConcurrentBag<Task>();
        var notificationCount = 0;
        var flushCount = 0;
        var flushInFlight = 0;
        var flushRequested = 0;
        var stopping = 0;

        void RequestFlush()
        {
            if (Volatile.Read(ref stopping) != 0) return;
            Interlocked.Exchange(ref flushRequested, 1);
            if (Interlocked.Exchange(ref flushInFlight, 1) != 0) return;
            flushTasks.Add(Task.Run(() =>
            {
                try
                {
                    do
                    {
                        Interlocked.Exchange(ref flushRequested, 0);
                        Interlocked.Increment(ref flushCount);
                        monitor.FlushPendingEvents();
                        monitor.RefreshExistingStreams();
                    } while (Volatile.Read(ref stopping) == 0 && Volatile.Read(ref flushRequested) != 0);
                }
                finally
                {
                    Volatile.Write(ref flushInFlight, 0);
                    if (Volatile.Read(ref flushRequested) != 0) RequestFlush();
                }
            }));
        }

        var followupGate = new object();
        var followupStage = 0;
        int[] followupGaps = [40, 80, 130];
        System.Threading.Timer? followup = null;
        followup = new System.Threading.Timer(_ =>
        {
            lock (followupGate)
            {
                if (Volatile.Read(ref stopping) != 0) return;
                RequestFlush();
                followupStage++;
                if (followupStage < followupGaps.Length)
                    followup!.Change(followupGaps[followupStage], Timeout.Infinite);
            }
        }, null, Timeout.Infinite, Timeout.Infinite);
        void SessionChanged()
        {
            if (Volatile.Read(ref stopping) != 0) return;
            Interlocked.Increment(ref notificationCount);
            RequestFlush();
            // A session-created callback may precede its Start trace event. A bounded
            // event-triggered burst catches delayed starts without an idle flush timer.
            lock (followupGate)
            {
                followupStage = 0;
                followup.Change(followupGaps[0], Timeout.Infinite);
            }
        }
        void EvidenceChanged()
        {
            evidenceChanged.Set();
            // Match the tray host's retry path: a transition racing the census must
            // request another read even after the finite session-flush burst has ended.
            if (monitor.NeedsExistingStreamRefresh) RequestFlush();
        }

        watcher.Changed += SessionChanged;
        monitor.Changed += EvidenceChanged;
        try
        {
            var ready = Stopwatch.StartNew();
            while (!watcher.IsReady && watcher.LastError is null && ready.Elapsed < TimeSpan.FromSeconds(3))
                Thread.Sleep(5);
            if (!watcher.IsReady)
                throw new InvalidOperationException(watcher.LastError ?? "Audio session notifications did not become ready.");
            monitor.Start();
            if (!monitor.IsRunning) throw new InvalidOperationException(monitor.Error);
            var testsWatch = Stopwatch.StartNew();
            for (var round = 1; round <= 2; round++)
            foreach (var rate in new[] { 44100, 48000, 96000, 192000 })
            {
                using var client = device.AudioClient;
                var notificationsBefore = Volatile.Read(ref notificationCount);
                var flushesBefore = Volatile.Read(ref flushCount);
                var watch = Stopwatch.StartNew();
                client.Initialize(AudioClientShareMode.Shared,
                    AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                    1_000_000, 0, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2), Guid.NewGuid());
                var render = client.AudioRenderClient;
                render.GetBuffer(client.BufferSize);
                render.ReleaseBuffer(client.BufferSize, AudioClientBufferFlags.Silent);
                int? detected = null;
                double? initializationLatency = null, startLatency = null;
                string? detail = null;
                var startedAt = watch.Elapsed.TotalMilliseconds;
                client.Start();
                try
                {
                    while (watch.Elapsed < TimeSpan.FromSeconds(4))
                    {
                        var frames = client.BufferSize - client.CurrentPadding;
                        if (frames > 0) { render.GetBuffer(frames); render.ReleaseBuffer(frames, AudioClientBufferFlags.Silent); }
                        var observation = monitor.Resolve(device.ID, [Environment.ProcessId], executable);
                        detail = observation.Detail;
                        if (observation.SampleRateHz == rate)
                        {
                            detected = observation.SampleRateHz;
                            initializationLatency = Math.Round(watch.Elapsed.TotalMilliseconds, 3);
                            startLatency = Math.Round(watch.Elapsed.TotalMilliseconds - startedAt, 3);
                            break;
                        }
                        // Wait for evidence while continuing to refill silence. These reads
                        // never flush ETW: only the Core Audio event path above does that.
                        evidenceChanged.WaitOne(5);
                    }
                }
                finally { client.Stop(); }
                allPassed &= detected == rate;
                rows.Add(new(round, rate, detected, detected == rate, initializationLatency, startLatency,
                    Volatile.Read(ref notificationCount) - notificationsBefore,
                    Volatile.Read(ref flushCount) - flushesBefore, detail));
            }
            testsWatch.Stop();
            var after = ReadMixRate(device);
            return new("Production stream detector; session-triggered ETW flush at 0/40/120/250 ms; silent shared streams; no endpoint writes",
                allPassed && before == after, device.FriendlyName, before, after,
                Math.Round(testsWatch.Elapsed.TotalMilliseconds, 3), rows);
        }
        finally
        {
            Volatile.Write(ref stopping, 1);
            watcher.Changed -= SessionChanged;
            watcher.Dispose();
            followup.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Task.WhenAll(flushTasks.ToArray()).GetAwaiter().GetResult();
            monitor.Changed -= EvidenceChanged;
            monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static int ReadMixRate(MMDevice device)
    { using var client = device.AudioClient; return client.MixFormat.SampleRate; }
}

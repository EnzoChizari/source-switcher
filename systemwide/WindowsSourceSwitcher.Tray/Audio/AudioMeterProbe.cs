using System.Diagnostics;
using WindowsLosslessSwitcher.Services;

namespace WindowsSourceSwitcher.Tray.Audio;

/// <summary>Explicit read-only metering diagnostic. Never opens a render/capture client.</summary>
internal static class AudioMeterProbe
{
    public static object Run()
    {
        var endpoints = new CoreAudioEndpointController();
        using var volumes = new AudioSessionVolumeController();
        var devices = new List<object>();
        foreach (var device in endpoints.GetRenderDevices())
        {
            var census = volumes.Read(device.Id);
            var summary = census.Sources.ToDictionary(source => source.Identity,
                source => new Summary(source.Name, source.Identity), StringComparer.OrdinalIgnoreCase);
            var gate = new object();
            var frameCount = 0;
            var generation = -1L;
            using var meter = new AudioSessionMeterMonitor(frame =>
            {
                if (frame.Generation != Volatile.Read(ref generation)) return;
                lock (gate)
                {
                    frameCount++;
                    foreach (var source in frame.Sources)
                    {
                        if (!summary.TryGetValue(source.Identity, out var row)) continue;
                        if (source.Peak is { } peak)
                        {
                            row.Samples++;
                            row.MinimumPeak = Math.Min(row.MinimumPeak ?? peak, peak);
                            row.MaximumPeak = Math.Max(row.MaximumPeak ?? peak, peak);
                            if (peak > 0) row.NonzeroSamples++;
                        }
                        else row.UnavailableSamples++;
                        if (source.Error is { } error && row.Errors.Count < 3) row.Errors.Add(error);
                    }
                }
            });
            generation = meter.SetSources(census);
            var watch = Stopwatch.StartNew();
            Thread.Sleep(2000);
            meter.Stop();
            int atStop;
            lock (gate) atStop = frameCount;
            Thread.Sleep(150);
            lock (gate)
            {
                devices.Add(new { device.Id, device.FriendlyName, CensusError = census.Error,
                    ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds, Frames = atStop,
                    AdditionalFramesAfterStop = frameCount - atStop, Sources = summary.Values.ToArray() });
            }
        }
        return new { Mode = "Read-only Windows session peak summaries; no audio capture, playback, volume or format writes",
            PollingIntervalMilliseconds = 50, Devices = devices };
    }

    private sealed class Summary(string name, string identity)
    {
        public string Name { get; } = name;
        public string Identity { get; } = identity;
        public int Samples { get; set; }
        public int NonzeroSamples { get; set; }
        public int UnavailableSamples { get; set; }
        public float? MinimumPeak { get; set; }
        public float? MaximumPeak { get; set; }
        public HashSet<string> Errors { get; } = new();
    }
}

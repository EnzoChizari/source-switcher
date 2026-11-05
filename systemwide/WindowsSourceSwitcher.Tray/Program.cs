using System.Text.Json;
using WindowsLosslessSwitcher.Services;
using WindowsLosslessSwitcher.Models;
using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;

namespace WindowsSourceSwitcher.Tray;

internal static class Program
{
    internal const string ProductName = "Source Switcher";
    internal static string DisplayVersion { get; } = typeof(Program).Assembly.GetName().Version is { } version
        ? version.ToString(3) : string.Empty;

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.Length == 1 && args[0] == "--shutdown")
            {
                try { _ = InstanceShutdownSignal.Request(); return 0; }
                catch (UnauthorizedAccessException) { return 1; }
            }
            // Diagnostic modes never compose a format-writing host.
            if (args.Length == 3 && args[0] == "--update-read" && Version.TryParse(args[1], out var installed))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var http = new HttpClient();
                var token = GitHubUpdateFeed.ReadCredentialAsync(timeout.Token).GetAwaiter().GetResult();
                var release = new GitHubUpdateFeed(http).CheckAsync(installed, token ?? string.Empty, timeout.Token).GetAwaiter().GetResult();
                WriteJson(args[2], new { CredentialAvailable = token is not null, Installed = installed.ToString(), Available = release?.Version.ToString() });
                return 0;
            }
            if (args.Length == 2 && args[0] == "--playback-read")
            {
                using var census = new AudioSessionEnumerator();
                var endpoint = new CoreAudioEndpointController();
                var sources = endpoint.GetRenderDevices().SelectMany(device => census.Enumerate(device.Id)).ToArray();
                var monitor = new FavoritePlaybackMonitor();
                var events = new List<object>();
                void Record()
                {
                    var snapshot = monitor.Snapshot;
                    lock (events) events.Add(new { TimestampUtc = DateTimeOffset.UtcNow, snapshot.Version,
                        Playback = snapshot.Resolve(sources).Select(item => new { Source = item.Key, State = item.Value.ToString() }).ToArray() });
                }
                monitor.Changed += (_, _) => Record();
                try
                {
                    monitor.StartAsync().GetAwaiter().GetResult();
                    Record();
                    Thread.Sleep(15000);
                    lock (events) WriteJson(args[1], new { Mode = "Read-only media playback notifications; no settings, transport or audio writes", Sources = sources, Events = events.ToArray() });
                    return 0;
                }
                finally { monitor.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            if (args.Length == 2 && args[0] == "--catalog-read")
            {
                var endpoint = new CoreAudioEndpointController();
                using var enumerator = new AudioSessionEnumerator();
                var source = endpoint.GetRenderDevices().SelectMany(device => enumerator.Enumerate(device.Id))
                    .FirstOrDefault(source => source.SupportsAutomaticRate);
                var observations = new List<NativeRateObservation>();
                var monitor = new AutomaticRateMonitor(new DiagnosticsLogger());
                monitor.RateObserved += (_, observation) => { lock (observations) observations.Add(observation); };
                try
                {
                    monitor.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    while (watch.Elapsed < TimeSpan.FromSeconds(12))
                    {
                        monitor.SelectSource(source, 0);
                        Thread.Sleep(250);
                    }
                    lock (observations) WriteJson(args[1], new { Mode = "Read-only Apple Music track/catalog estimate; no settings or endpoint writes", Source = source, Observations = observations.ToArray() });
                    return 0;
                }
                finally { monitor.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            if (args.Length == 2 && args[0] == "--register-browser-helper")
            {
                WriteJson(args[1], new { Status = BrowserBridgeInstaller.Register(AppContext.BaseDirectory) });
                return 0;
            }
            if (args.Length == 2 && args[0] == "--browser-bridge-read")
            {
                var snapshots = new List<BrowserDecoderSnapshot>();
                var sync = new object();
                var bridge = new BrowserRateBridge(Path.Combine(AppContext.BaseDirectory, "browser-helper", "WindowsSourceSwitcher.BrowserHost.exe"));
                bridge.Changed += () => { lock (sync) snapshots.Add(bridge.Snapshot); };
                try
                {
                    bridge.StartAsync().GetAwaiter().GetResult();
                    Thread.Sleep(20000);
                    BrowserDecoderSnapshot[] results;
                    lock (sync) results = snapshots.ToArray();
                    WriteJson(args[1], new { Mode = "Read-only browser metadata receiver; no settings, playback or audio writes", Snapshots = results, Final = bridge.Snapshot });
                    return 0;
                }
                finally { bridge.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            if (args.Length == 2 && args[0] == "--device-watch-read")
            {
                using var watcher = new AudioDeviceWatcher();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (!watcher.IsReady && !watcher.Completion.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(2))
                    Thread.Sleep(10);
                var ready = watcher.IsReady;
                var startupMs = watch.Elapsed.TotalMilliseconds;
                watcher.Dispose();
                var completed = watcher.Completion.Wait(TimeSpan.FromSeconds(2));
                WriteJson(args[1], new { Mode = "Read-only native endpoint notification registration and disposal; no device changes",
                    Ready = ready, StartupMs = startupMs, CleanupCompleted = completed, Error = watcher.LastError });
                return ready && completed && watcher.LastError is null ? 0 : 1;
            }
            if (args.Length == 2 && args[0] == "--meter-read")
            {
                WriteJson(args[1], AudioMeterProbe.Run());
                return 0;
            }
            if (args.Length == 2 && args[0] == "--stream-read")
            {
                var endpoints = new CoreAudioEndpointController();
                using var sessions = new AudioSessionEnumerator();
                var streamMonitor = new WasapiStreamRateMonitor();
                try
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    streamMonitor.Start();
                    streamMonitor.RefreshExistingStreams();
                    var devices = endpoints.GetRenderDevices().Select(device => new
                    {
                        device.Id, device.FriendlyName,
                        Format = endpoints.GetCurrentDeviceFormat(device.Id),
                        MixFormat = endpoints.GetCurrentMixFormat(device.Id),
                        Sources = sessions.Enumerate(device.Id).Select(source => new
                        {
                            source.ProcessName, source.Identity, source.ProcessIds, source.IsActive,
                            Stream = source.ExecutablePath is null ? null : streamMonitor.Resolve(device.Id,
                                source.ProcessIds, source.ExecutablePath, source.SessionInstanceIds, source.ProcessExecutablePaths)
                        }).ToArray()
                    }).ToArray();
                    WriteJson(args[1], new { Mode = "Read-only existing stream census; no playback, volume or format writes",
                        Milliseconds = watch.Elapsed.TotalMilliseconds, Error = streamMonitor.Error, Devices = devices });
                    return streamMonitor.IsRunning ? 0 : 1;
                }
                finally { streamMonitor.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            if (args.Length == 2 && args[0] == "--mixer-inspect")
            {
                var endpoints = new CoreAudioEndpointController();
                using var volumes = new AudioSessionVolumeController();
                WriteJson(args[1], new { Mode = "Read-only Windows app volumes; no volume or format writes",
                    Devices = endpoints.GetRenderDevices().Select(device => new {
                        device.Id, device.FriendlyName, Census = volumes.Read(device.Id) }).ToArray() });
                return 0;
            }
            if (args.Length == 2 && args[0] == "--stream-probe")
            {
                var result = StreamDetectionProbe.Run();
                WriteJson(args[1], result);
                return result.Passed ? 0 : 1;
            }
            if (args.Length == 2 && args[0] == "--inspect")
            {
                var endpoints = new CoreAudioEndpointController();
                using var sessions = new AudioSessionEnumerator();
                var devices = endpoints.GetRenderDevices().Select(device => new
                {
                    device.Id, device.FriendlyName, device.IsDefault,
                    Format = endpoints.GetCurrentDeviceFormat(device.Id),
                    MixFormat = endpoints.GetCurrentMixFormat(device.Id),
                    SupportedFormats = endpoints.GetSupportedFormats(device.Id, forceRefresh: true),
                    ProbeDiagnostics = endpoints.GetLastProbeDiagnostics(device.Id),
                    Sources = sessions.Enumerate(device.Id),
                    Error = sessions.LastError,
                }).ToArray();
                WriteJson(args[1], new { Mode = "Read-only inspection", Devices = devices });
                return 0;
            }
            if (args.Length == 2 && args[0] == "--smoke-test")
            {
                using var popup = new SourcePopup();
                var example = new PlatformAudioSource(@"C:\EXAMPLE\MUSIC.EXE", "Music (example)", @"C:\Example\Music.exe", [100], true, true, "demo");
                var settings = new SourceSettings { TargetEndpointId = "example" };
                settings.SelectMaster(example.Identity, example.ProcessName);
                settings.SetFavorite(example.Identity, example.ProcessName, true);
                settings.SetFavorite(@"C:\Example\Saved.exe", "Saved player (example)", true);
                settings.ManualSampleRatesHz[example.Identity] = 44100;
                popup.Render([new AudioDeviceInfo("example", "TOPPING DX5 II (example preview)", true)],
                    [example, new PlatformAudioSource(@"C:\EXAMPLE\BROWSER.EXE", "Browser (example)", @"C:\Example\Browser.exe", [200], true, true, "demo2")],
                    [new(44100, 32, 2), new(48000, 32, 2), new(96000, 32, 2)], new(48000, 32, 2), settings,
                    "Read-only example preview. No audio settings have been changed.",
                    "Manual override: 44.1 kHz. This is a user setting, not a detected rate.", false);
                // Invisible Show creates child handles and completes real WinForms layout.
                popup.Opacity = 0;
                popup.Location = new Point(-20000, -20000);
                popup.Show();
                popup.PerformLayout();
                popup.Refresh();
                var screenshot = Path.ChangeExtension(Path.GetFullPath(args[1]), ".png");
                SavePopupImage(popup, screenshot);

                // Repaint the same source after removing its override, with the unrelated
                // Apple preference enabled, to cover the confusing state reported by users.
                settings.ManualSampleRatesHz.Clear();
                settings.CatalogRateDetectionEnabled = true;
                popup.Render([new AudioDeviceInfo("example", "TOPPING DX5 II (example preview)", true)],
                    [example], [new(44100, 32, 2), new(48000, 32, 2)], new(44100, 32, 2), settings,
                    "Read-only example preview. Choose a manual rate for this app.",
                    SourceRatePresentation.Describe(example, settings).Detail, false);
                var waitingScreenshot = Path.ChangeExtension(screenshot, "waiting.png");
                SavePopupImage(popup, waitingScreenshot);

                var applePath = @"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.2.3.4_x64__nzyj5cx40ttqa\AppleMusic.exe";
                var appleExample = new PlatformAudioSource(applePath.ToUpperInvariant(), "Apple Music (example)", applePath,
                    [300], true, true, "apple-demo");
                settings.SelectMaster(appleExample.Identity, appleExample.ProcessName);
                var observation = new RateObservation(appleExample.Identity, appleExample.SessionToken, settings.SelectionRevision,
                    96000, RateProvenance.CatalogMetadata, "Example catalog result; no network lookup was made.");
                popup.Render([new AudioDeviceInfo("example", "TOPPING DX5 II (example preview)", true)],
                    [appleExample], [new(44100, 32, 2), new(48000, 32, 2), new(96000, 32, 2)], new(44100, 32, 2), settings,
                    "Read-only example preview. Catalog estimates do not measure playback.",
                    SourceRatePresentation.Describe(appleExample, settings, observation).Detail, false, observation);
                var catalogScreenshot = Path.ChangeExtension(screenshot, "catalog.png");
                SavePopupImage(popup, catalogScreenshot);

                settings.RememberAppliedRate(appleExample.Identity, 44100);
                var pausedApple = appleExample with { IsActive = false };
                popup.Render([new AudioDeviceInfo("example", "TOPPING DX5 II (example preview)", true)],
                    [pausedApple], [new(44100, 32, 2), new(96000, 32, 2)], new(44100, 32, 2), settings,
                    "Paused: holding 44.1 kHz output. Track estimate remains independent.",
                    SourceRatePresentation.Describe(pausedApple, settings, observation,
                        playback: FavoritePlaybackState.NotPlaying).Detail, false, observation,
                    playback: new Dictionary<string, FavoritePlaybackState> { [pausedApple.Identity] = FavoritePlaybackState.NotPlaying });
                var pausedCatalogScreenshot = Path.ChangeExtension(screenshot, "paused-catalog.png");
                SavePopupImage(popup, pausedCatalogScreenshot);

                settings.SelectMaster(example.Identity, example.ProcessName);
                var streamObservation = new RateObservation(example.Identity, example.SessionToken, settings.SelectionRevision,
                    48000, RateProvenance.WasapiStream, "Example stream evidence; no live trace in this UI preview.");
                popup.Render([new AudioDeviceInfo("example", "TOPPING DX5 II (example preview)", true)],
                    [example], [new(44100, 32, 2), new(48000, 32, 2)], new(44100, 32, 2), settings,
                    "Read-only example preview. Application stream rate detected.",
                    SourceRatePresentation.Describe(example, settings, streamObservation: streamObservation).Detail,
                    false, streamObservations: new Dictionary<string, RateObservation> { [example.Identity] = streamObservation });
                var streamScreenshot = Path.ChangeExtension(screenshot, "stream.png");
                SavePopupImage(popup, streamScreenshot);

                settings.RememberAppliedRate(example.Identity, 48000);
                var pausedExample = example with { IsActive = false };
                popup.Render([new AudioDeviceInfo("example", "TOPPING DX5 II (example preview)", true)],
                    [pausedExample], [new(44100, 32, 2), new(48000, 32, 2)], new(48000, 32, 2), settings,
                    "Read-only example preview. Paused master retains its confirmed rate.",
                    SourceRatePresentation.Describe(pausedExample, settings).Detail, false);
                var heldScreenshot = Path.ChangeExtension(screenshot, "held.png");
                SavePopupImage(popup, heldScreenshot);
                // The mixer preview uses data only; it has no controller or write handler.
                var volumeIdentity = new VolumeSessionIdentity("example", example.Identity, 100,
                    DateTimeOffset.UtcNow.AddMinutes(-1), example.ExecutablePath, "volume-demo", false);
                var mixedIdentity = volumeIdentity with { SourceIdentity = "browser-volume", ProcessId = 200,
                    ExecutablePath = @"C:\Example\Browser.exe", SessionInstanceId = "browser-volume-demo" };
                popup.RenderMixer(new("example", [
                    new("example", example.Identity, "Music (example)", .55f, false, false, false,
                        [volumeIdentity], true, true, null),
                    new("example", "browser-volume", "Browser (example)", null, null, true, true,
                        [mixedIdentity, mixedIdentity with { SessionInstanceId = "browser-volume-demo-2" }], true, true, null),
                    new("example", "shared-volume", "Shared session (example)", .7f, false, false, false,
                        [], false, true, "Windows reports a shared session; per-app control is read only.") ], null));
                popup.SelectMixerTab();
                popup.RenderMeters(new("example", 1, 1, System.Diagnostics.Stopwatch.GetTimestamp(), [
                    new(example.Identity, [volumeIdentity], .16f, null),
                    new("browser-volume", [mixedIdentity, mixedIdentity with { SessionInstanceId = "browser-volume-demo-2" }], 0f, null),
                    new("shared-volume", [], null, "Per-app peak is unavailable for this shared session.") ], null));
                var mixerScreenshot = Path.ChangeExtension(screenshot, "mixer.png");
                SavePopupImage(popup, mixerScreenshot);
                popup.SelectControlsTab();
                var controlsScreenshot = Path.ChangeExtension(screenshot, "controls.png");
                SavePopupImage(popup, controlsScreenshot);
                popup.SelectDiagnosticsTab();
                popup.RenderDiagnostics(new([
                    new("Evidence", "Observed submitted PCM; original media unverified"),
                    new("Submitted PCM", "48 kHz"), new("Windows mix", "48 kHz · 32-bit · 2 channels"),
                    new("Post-switch session", "Active Windows session observed after the rate change. Audibility is unverified."),
                    new("Resampling", "No PCM/mixer mismatch; DSP and source conversion remain possible")
                ], [new(1, DateTimeOffset.UtcNow, "Example", "Read-only diagnostics preview; no physical interruption measurement.")]));
                var diagnosticsScreenshot = Path.ChangeExtension(screenshot, "diagnostics.png");
                SavePopupImage(popup, diagnosticsScreenshot);
                popup.Hide();
                // Exercise the real tray timer/message-loop/disposal path with a hard write
                // guard, disabled in-memory settings, and no load/save of the user's settings.
                using var smokeContext = new TrayContext(diagnosticsMode: true);
                using var autoExit = new System.Windows.Forms.Timer { Interval = 2000 };
                autoExit.Tick += async (_, _) => { autoExit.Stop(); await smokeContext.ShutdownAsync(); };
                autoExit.Start();
                Application.Run(smokeContext);
                WriteJson(args[1], new { Mode = "Example UI rendering and tray lifecycle; no audio writes", popup.Text,
                    Width = popup.Width, Height = popup.Height, Screenshot = screenshot,
                    WaitingForStreamScreenshot = waitingScreenshot, CatalogEstimateScreenshot = catalogScreenshot,
                    PausedCatalogEstimateScreenshot = pausedCatalogScreenshot,
                    StreamRateScreenshot = streamScreenshot, HeldRateScreenshot = heldScreenshot,
                    MixerScreenshot = mixerScreenshot, ControlsScreenshot = controlsScreenshot, DiagnosticsScreenshot = diagnosticsScreenshot });
                return 0;
            }
            var startHidden = args.Length == 1 && args[0] == "--background";
            if (args.Length != 0 && !startHidden)
                throw new ArgumentException("Usage: WindowsSourceSwitcher.Tray.exe [--inspect output.json | --mixer-inspect output.json | --meter-read output.json | --stream-read output.json | --smoke-test output.json | --stream-probe output.json]");

            using var instance = new Mutex(true, @"Local\DX5SourceSwitcher", out var firstInstance);
            if (!firstInstance)
            {
                if (!startHidden) MessageBox.Show(ProductName + " is already running. Open it from the system tray.",
                    ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            using var context = new TrayContext(startHidden: startHidden);
            using var shutdown = new InstanceShutdownSignal(context.RequestShutdown);
            Application.Run(context);
            return 0;
        }
        catch (Exception) when (args.Length == 3 && args[0] == "--update-read")
        {
            WriteJson(args[2], new { Error = "Could not read the release feed." });
            return 1;
        }
        catch (Exception ex)
        {
            if (args.Length == 2)
            {
                WriteJson(args[1], new { Error = ex.ToString() });
            }
            else
                MessageBox.Show(ex.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void SavePopupImage(SourcePopup popup, string path)
    {
        popup.PerformLayout();
        popup.Refresh();
        using var bitmap = new Bitmap(popup.Width, popup.Height);
        popup.DrawToBitmap(bitmap, new Rectangle(Point.Empty, popup.Size));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bitmap.Save(path);
    }

    private static void WriteJson(string path, object value)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}

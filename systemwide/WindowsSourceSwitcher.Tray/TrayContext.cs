using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;

namespace WindowsSourceSwitcher.Tray;

internal sealed class TrayContext : ApplicationContext
{
    private readonly SourcePopup _popup = new();
    private readonly NotifyIcon _icon;
    private Icon? _trayArtwork;
    private int _trayThemePosted;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    private readonly System.Windows.Forms.Timer _notificationTimer = new() { Interval = 50 };
    private readonly System.Windows.Forms.Timer _stabilityTimer = new() { Interval = 180 };
    private readonly System.Windows.Forms.Timer _traceFlushTimer = new() { Interval = 40 };
    private readonly System.Windows.Forms.Timer _mixerPollTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _mixerWorkTimer = new() { Interval = 40 };
    private readonly System.Windows.Forms.Timer _meterExpiryTimer = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _favoriteAutoTimer = new();
    private readonly SourcePrioritySwitchPolicy _favoriteAutoPolicy = new();
    private bool _priorityHandoffAfterFullscreen;
    private readonly FullscreenMasterPolicy _fullscreenPolicy = new();
    private readonly FullscreenAppMonitor _fullscreenMonitor = new();
    private string _fullscreenStatus = "Fullscreen app priority is off.";
    private bool _automaticMasterChange;
    private nint _fullscreenSuppressedWindow;
    private string? _fullscreenRetainedSourceKey;
    private readonly SessionContinuityTracker _sessionContinuity = new();
    private readonly PinnedOutputConnection _outputConnection = new();
    private readonly CancellationTokenSource _appleSwitchLifetime = new();
    private string _appleSwitchDetail = "Apple Music switches while rendering; signal and timeline recovery are checked.";
    private string? _appleSwitchFailureKey;
    private readonly FavoritePlaybackMonitor _playback = new();
    private Task _playbackStartup = Task.CompletedTask;
    private IReadOnlyDictionary<string, FavoritePlaybackState> _mediaPlayback = new Dictionary<string, FavoritePlaybackState>();
    private int _playbackNotificationPosted;
    private string _favoriteAutoStatus = "Automatic favorite switching is off.";
    private readonly AudioSessionVolumeController _volumes = new();
    private readonly GlobalHotkeyController _hotkeys = new();
    private Task _favoriteCycleTask = Task.CompletedTask;
    private long _hotkeyGeneration;
    private readonly ControlEventHistory _history = new();
    private readonly FormatChangeThrottle _formatThrottle = new();
    private readonly long _startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
    private RateDecision? _lastDecision;
    private BrowserRateBridge? _browserBridge;
    private RateObservation? _browserObservation;
    private Task _browserConfigurationTask = Task.CompletedTask;
    private int _browserNotificationPosted;
    private AudioDeviceWatcher? _deviceWatcher;
    private DateTimeOffset _deviceWatcherRetryAfter;
    private string? _notificationEndpoint;
    private int _deviceNotificationPosted, _pinnedDeviceResetPending;
    private AudioSessionMeterMonitor? _meters;
    private AudioMeterFrame? _pendingMeterFrame;
    private long _meterGeneration = -1;
    private int _meterFramePosted;
    private readonly Dictionary<string, VolumeMixerChange> _mixerChanges = new(StringComparer.OrdinalIgnoreCase);
    private bool _mixerWorking, _mixerReadRequested;
    private int _mixerNotificationPosted;
    private string? _mixerError;
    private Task _mixerTask = Task.CompletedTask;
    private readonly CoreAudioEndpointController _endpoint = new();
    private readonly AudioSessionEnumerator _enumerator = new();
    private readonly SettingsStore _store;
    private readonly SourceSettings _settings;
    private readonly string _logPath;
    private readonly bool _diagnosticsMode;
    private AutomaticRateMonitor? _monitor;
    private WasapiStreamRateMonitor? _streamMonitor;
    private readonly StreamObserverRecovery _streamRecovery = new();
    private bool _streamFailureReported;
    private volatile bool _suspended;
    private long _powerGeneration;
    private long _formatOperationGeneration;
    private long _applePreferenceGeneration;
    private bool _powerResumePending;
    private string _powerStatus = "Ready; waiting for Windows power notifications.";
    private AudioSessionWatcher? _sessionWatcher;
    private string? _watchedEndpointId;
    private DateTimeOffset _watcherRetryAfter;
    private int _postedRefresh, _postedCensus, _flushInFlight;
    private bool _refreshPending, _censusPending;
    private int _traceFlushPass;
    private Task _flushTask = Task.CompletedTask;
    private IReadOnlyDictionary<string, RateObservation> _streamObservations = new Dictionary<string, RateObservation>();
    private string? _streamDetail;
    private bool _streamRetryRequested;
    private RateObservation? _observation;
    private NativeRateObservation? _appleCatalogObservation;
    private long _observationGeneration = -1;
    private long _epoch;
    private static readonly HttpClient UpdateHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly GitHubUpdateFeed _updateFeed = new(UpdateHttp);
    private readonly CancellationTokenSource _updateLifetime = new();
    private PortableRelease? _availableUpdate;
    private bool _updateBusy;
    private bool _refreshing, _applying, _exiting, _reprobe = true, _targetWasAvailable;
    private string? _saveError, _warning, _candidateKey, _failedKey;
    private long _candidateSince;
    private bool _chooseInitialOutput;
    private string _status = "Finding your Windows audio outputs…";
    private string _evidence = "Native rate: unknown. Set a per-app override to choose a rate.";
    private Snapshot _snapshot = new([], [], [], null, null, false, null);
    private Task _refreshTask = Task.CompletedTask;
    private Task _saveTask = Task.CompletedTask;
    private FirstRunTutorial? _tutorial;

    public TrayContext(bool diagnosticsMode = false, bool startHidden = false)
        : this(diagnosticsMode, startHidden,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DX5SourceSwitcher"))
    {
    }

    internal TrayContext(bool diagnosticsMode, bool startHidden, string directory)
    {
        _diagnosticsMode = diagnosticsMode;
        _store = new SettingsStore(Path.Combine(directory, "settings.json"));
        _logPath = Path.Combine(directory, "switcher.log");
        var loaded = diagnosticsMode ? new SettingsLoadResult(new SourceSettings { SwitchingEnabled = false }, null)
            : _store.LoadAsync().GetAwaiter().GetResult();
        _settings = loaded.Settings;
        _chooseInitialOutput = loaded.IsNew;
        _notificationEndpoint = _settings.TargetEndpointId;
        _warning = loaded.Warning;
        var migrated = SourceExclusions.ApplyToSettings(_settings);
        migrated |= AppleMusicIdentity.MigrateSettings(_settings);
        if (_settings.BrowserRateDetectionEnabled && !BrowserBridgeInstaller.IsAvailable(AppContext.BaseDirectory))
        {
            _settings.BrowserRateDetectionEnabled = false;
            migrated = true;
        }
        if (migrated) _saveTask = SaveAsync();

        _trayArtwork = TrayIconArtwork.CreateForCurrentTheme();
        _icon = new NotifyIcon { Icon = _trayArtwork, Text = Program.ProductName, Visible = true };
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += TrayThemeChanged;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => _popup.ShowAtTray());
        menu.Items.Add("Volume mixer", null, (_, _) => { _popup.SelectMixerTab(); _popup.ShowAtTray(); });
        menu.Items.Add("Quick tour", null, (_, _) => ShowTutorial());
        menu.Items.Add("Exit", null, async (_, _) => await ShutdownAsync());
        MinimalDarkTheme.Apply(menu);
        _icon.ContextMenuStrip = menu;
        TrayIconActivation.OpenOnLeftClick(_icon, _popup.ShowAtTray);
        _hotkeys.Invoked += HandleHotkey;
        _hotkeys.StatusChanged += () => _popup.SetHotkeyStatus(_hotkeys.Status);
        _popup.GlobalHotkeysChanged += enabled =>
        {
            if (_exiting || _suspended || _diagnosticsMode) return;
            _hotkeyGeneration++;
            _settings.GlobalHotkeysEnabled = enabled;
            _hotkeys.SetEnabled(enabled);
            _saveTask = SaveAsync();
        };
        _popup.FavoriteAutoSwitchChanged += enabled => Change(() => _settings.FavoriteAutoSwitchEnabled = enabled);
        _popup.FullscreenAutoSwitchChanged += SetFullscreenPriority;
        _popup.HotkeyRetryRequested += () => { if (!_exiting) { _hotkeyGeneration++; _hotkeys.Retry(); } };
        if (!diagnosticsMode) _hotkeys.SetEnabled(_settings.GlobalHotkeysEnabled);
        _popup.SetHotkeyStatus(_hotkeys.Status);
        _popup.DiagnosticsVisibilityChanged += () => { if (_popup.DiagnosticsVisible) RenderDiagnostics(); };

        _popup.DeviceSelected += SelectOutput;
        _popup.SourceSelected += source =>
        {
            if (!source.CanPersist)
            {
                _status = "This process's executable path is unavailable. Choose another source.";
                _popup.InvalidateSourceRows();
                Render();
                return;
            }
            if (source.Identity == _settings.SelectedSourceKey) return;
            Change(() => { _settings.SelectMaster(source.Identity, source.ProcessName); InvalidateObservation(); });
        };
        _popup.SavedSourceSelected += (key, _) =>
        {
            // Saved rows are preferences, never invented live audio sessions. A selected
            // favorite still needs fresh session evidence before automatic switching.
            if (!(_settings.FavoriteSources.TryGetValue(key, out var name) || _settings.PinnedSources.TryGetValue(key, out name))
                || !SourceFavorites.CanFavorite(key, name)
                || StringComparer.OrdinalIgnoreCase.Equals(key, _settings.SelectedSourceKey)) return;
            Change(() => { _settings.SelectMaster(key, name); InvalidateObservation(); });
        };
        _popup.FavoriteChanged += (key, name, favorite) =>
        {
            if (_exiting || _suspended || _diagnosticsMode) return;
            try
            {
                if (!_settings.SetFavorite(key, name, favorite)) return;
                RequestRefresh();
                // Favorites are presentation preferences. Do not invalidate stream evidence,
                // advance the source revision, cancel a pending rate, or touch mixer values.
                _saveTask = SaveFavoritesAsync();
                Render();
                RequestRefresh();
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                _warning = error.Message;
                Render();
            }
        };
        _popup.PinChanged += (key, name, pinned) =>
        {
            if (_exiting || _diagnosticsMode || _applying) return;
            try
            {
                if (!_settings.SetPinned(key, name, pinned)) return;
                _saveTask = SaveFavoritesAsync();
                RequestRefresh();
            }
            catch (Exception error) { _status = "Could not save pin: " + error.Message; Render(); }
        };
        _popup.RateSelected += rate => Change(() =>
        {
            if (_settings.SelectedSourceKey is not { } key) return;
            if (rate is { } hz) _settings.ManualSampleRatesHz[key] = hz;
            else _settings.ManualSampleRatesHz.Remove(key);
        });
        _popup.SwitchingEnabledChanged += enabled => Change(() => _settings.SwitchingEnabled = enabled);
        _popup.CatalogChanged += enabled => Change(() => { _settings.CatalogRateDetectionEnabled = enabled; InvalidateObservation(); });
        _popup.StreamDetectionChanged += enabled => Change(() =>
        {
            _settings.StreamRateDetectionEnabled = enabled;
            _streamObservations = new Dictionary<string, RateObservation>();
            _streamRetryRequested = true;
        });
        _popup.BrowserIntegrationChanged += enabled => Change(() =>
        {
            _settings.BrowserRateDetectionEnabled = enabled;
            _browserConfigurationTask = ConfigureBrowserAsync();
        });
        _popup.BrowserRegistrationRequested += () =>
        {
            if (_exiting || _diagnosticsMode) return;
            try { _popup.SetBrowserStatus(BrowserBridgeInstaller.Register(AppContext.BaseDirectory)); }
            catch (Exception error) { _popup.SetBrowserStatus("Browser helper registration failed: " + error.Message); }
        };
        _popup.ReleaseRequested += () => Change(() =>
        {
            _settings.SelectedSourceKey = _settings.SelectedSourceDisplayName = _settings.LastKnownSourceKey = null;
            _settings.LastKnownSampleRateHz = null;
            _settings.SelectionRevision++;
            InvalidateObservation();
        });
        _popup.RetryRequested += () =>
        {
            _formatThrottle.Reset();
            _stabilityTimer.Interval = 180;
            _failedKey = _candidateKey = null;
            _stabilityTimer.Stop();
            _reprobe = true;
            _warning = null;
            _streamRetryRequested = true;
            _saveTask = SaveAsync();
            if (_monitor is not null) _ = RetryCatalogAsync();
            _sessionWatcher?.RefreshSubscriptions();
            _mixerError = null;
            RequestMixerRead();
            // Rescan and flush the existing trace. Restarting a healthy observer discards
            // initialization events that an already-playing client will not emit again.
            SessionChanged();
        };
        _popup.PowerEventReceived += HandlePowerEvent;
        _popup.VolumeChangeRequested += QueueMixerChange;
        _popup.MixerVisibilityChanged += () =>
        {
            if (_popup.MixerVisible)
            {
                _mixerPollTimer.Start();
                _meterExpiryTimer.Start();
                RequestMixerRead();
            }
            else
            {
                _mixerPollTimer.Stop();
                _mixerReadRequested = false;
                StopMeterPolling();
            }
        };
        _mixerPollTimer.Tick += (_, _) => RequestMixerRead();
        _meterExpiryTimer.Tick += (_, _) => _popup.ExpireMeters();
        _mixerWorkTimer.Tick += (_, _) => { _mixerWorkTimer.Stop(); StartMixerWork(); };
        _timer.Tick += (_, _) => { _sessionWatcher?.RefreshSubscriptions(); StartRefresh(); };
        _notificationTimer.Tick += (_, _) => { _notificationTimer.Stop(); StartRefresh(census: false); };
        _stabilityTimer.Tick += (_, _) => { _stabilityTimer.Stop(); StartRefresh(census: true); };
        _favoriteAutoTimer.Tick += (_, _) => { _favoriteAutoTimer.Stop(); StartRefresh(census: true); };
        _traceFlushTimer.Tick += (_, _) =>
        {
            _traceFlushTimer.Stop();
            RequestTraceFlush();
            // Start metadata can be emitted after the session-state callback. A finite
            // 0/40/120/250ms burst handles that race without a high-frequency idle timer.
            _traceFlushPass++;
            if (_traceFlushPass < 3)
            {
                _traceFlushTimer.Interval = _traceFlushPass == 1 ? 80 : 130;
                _traceFlushTimer.Start();
            }
        };
        _popup.VisibleChanged += (_, _) =>
        {
            _timer.Interval = _popup.Visible ? 3000 : 8000;
            if (!_popup.Visible || _exiting || _popup.Disposing) return;
            StartRefresh();
            if (!_diagnosticsMode && _settings.TutorialPending && _tutorial is null)
                _popup.BeginInvoke(new Action(() =>
                {
                    if (_popup.Visible && _settings.TutorialPending) ShowTutorial();
                }));
        };
        Render();
        _ = _popup.Handle;
        if (!diagnosticsMode) _browserConfigurationTask = ConfigureBrowserAsync();
        if (!diagnosticsMode)
        {
            _fullscreenMonitor.Changed += FullscreenChanged;
            _fullscreenMonitor.SetEnabled(_settings.FullscreenAutoSwitchEnabled && _settings.SwitchingEnabled);
        }
        if (!diagnosticsMode)
        {
            _playback.Changed += PlaybackChanged;
            _playbackStartup = _playback.StartAsync();
        }
        if (!diagnosticsMode && !startHidden) _popup.ShowAtTray();
        else _ = _popup.Handle;
        _timer.Interval = _popup.Visible ? 3000 : 8000;
        _timer.Start();
        _popup.BeginInvoke(new Action(() => StartRefresh()));
        if (!diagnosticsMode && PortableUpdateInstaller.CanInstall)
        {
            _popup.UpdateCheckRequested += async () => await RunUpdateAsync();
            _popup.UpdateInstallRequested += async () => await RunUpdateAsync(install: true);
            _popup.BeginInvoke(new Action(async () => await RunUpdateAsync(startup: true)));
        }
    }

    private async Task RunUpdateAsync(bool install = false, bool startup = false)
    {
        if (_exiting || _updateBusy || (install && _availableUpdate is null)) return;
        _updateBusy = true;
        _popup.SetUpdateStatus(install ? "Downloading and verifying the update…" : "Checking GitHub…", false, false);
        string status;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var token = await GitHubUpdateFeed.ReadCredentialAsync(timeout.Token);
            if (token is null)
                status = "GitHub sign-in is required. See START HERE.txt for private repository setup.";
            else if (install && _availableUpdate is { } release)
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                await PortableUpdateInstaller.PrepareAndScheduleAsync(_updateFeed, release, token, timeout.Token);
                if (!_exiting) await ShutdownAsync();
                return;
            }
            else
            {
                _availableUpdate = await _updateFeed.CheckAsync(typeof(Program).Assembly.GetName().Version!, token, timeout.Token);
                status = _availableUpdate is { } available
                    ? $"v{available.Version} is available. Update restarts Source Switcher."
                    : $"Source Switcher v{Program.DisplayVersion} is up to date.";
                if (startup && _availableUpdate is not null && !_exiting)
                    _icon.ShowBalloonTip(5000, Program.ProductName, status + " Open Controls to install.", ToolTipIcon.Info);
            }
        }
        catch (OperationCanceledException) { status = "The update request timed out. Try again."; }
        catch (InvalidDataException) { status = "The release package could not be verified."; }
        catch (Exception) { status = install ? "The update could not be prepared. Check access, free space, and the app folder."
            : "Could not check GitHub. Check your connection and private repository access."; }
        finally { _updateBusy = false; }

        if (!_exiting) _popup.SetUpdateStatus(status, true, _availableUpdate is not null);
    }

    private void ShowTutorial()
    {
        if (_exiting || _suspended || _diagnosticsMode) return;
        if (!_popup.Visible) _popup.ShowAtTray();
        if (_tutorial is not null)
        {
            _tutorial.Focus();
            return;
        }
        _tutorial = _popup.CreateTutorial(FinishTutorial);
        _tutorial.Start();
    }

    private void FinishTutorial()
    {
        _tutorial?.Dispose();
        _tutorial = null;
        if (!_exiting && _settings.TutorialPending)
        {
            _settings.TutorialPending = false;
            _saveTask = SaveAsync();
        }
    }

    // Runs on the UI thread via this app's always-created top-level HWND.
    // Automatic resume is sufficient; the subsequent user-resume broadcast is a duplicate.
    internal void HandlePowerEvent(int powerEvent)
    {
        if (_exiting || powerEvent is not (4 or 7 or 18)) return;
        if (powerEvent == 7 && !_suspended) return;
        if (powerEvent == 4 && _suspended) return;
        _epoch++;
        Interlocked.Increment(ref _powerGeneration);
        _candidateKey = _failedKey = null;
        _streamObservations = new Dictionary<string, RateObservation>();
        _streamMonitor?.InvalidateExistingStreams();
        InvalidateObservation();
        _favoriteAutoPolicy.SuspendForRecovery(System.Diagnostics.Stopwatch.GetElapsedTime(_startedAt), TimeSpan.Zero);
        RecordSessionContinuity(_sessionContinuity.Cancel());
        _notificationTimer.Stop();
        _stabilityTimer.Stop();
        _favoriteAutoTimer.Stop();
        _traceFlushTimer.Stop();
        _mixerWorkTimer.Stop();
        foreach (var change in _mixerChanges.Values)
            _popup.CompleteMixerWrite(change, new(false, 0, 0, "Power state changed; refresh the current session.", null));
        _mixerChanges.Clear();
        _mixerReadRequested = false;
        StopMeterPolling();
        _suspended = powerEvent == 4;
        _popup.SetPowerSuspended(_suspended);
        if (_suspended)
        {
            _timer.Stop();
            _mixerPollTimer.Stop();
            _fullscreenMonitor.SetEnabled(false);
            _powerStatus = "Suspended; holding the selected output, source and rate.";
        }
        else
        {
            _powerResumePending = true;
            _reprobe = true;
            _targetWasAvailable = false;
            _watchedEndpointId = null;
            _watcherRetryAfter = _deviceWatcherRetryAfter = DateTimeOffset.MinValue;
            _streamRetryRequested = true;
            _powerStatus = "Resuming; reading fresh devices, formats and audio sessions.";
            _timer.Start();
            if (!_diagnosticsMode) _fullscreenMonitor.SetEnabled(_settings.FullscreenAutoSwitchEnabled && _settings.SwitchingEnabled);
            if (_popup.MixerVisible) { _mixerPollTimer.Start(); RequestMixerRead(); }
            StartRefresh(census: true);
        }
        _status = _powerStatus;
        Log(_powerStatus, "Power");
        Render();
    }

    private void HandleHotkey(TrayHotkeyAction action)
    {
        if (_exiting || _suspended || _diagnosticsMode || !_settings.GlobalHotkeysEnabled) return;
        switch (action)
        {
            case TrayHotkeyAction.OpenSources:
                _popup.SelectSourcesTab();
                _popup.ShowAtTray();
                break;
            case TrayHotkeyAction.OpenMixer:
                _popup.SelectMixerTab();
                _popup.ShowAtTray();
                break;
            case TrayHotkeyAction.PreviousFavorite:
            case TrayHotkeyAction.NextFavorite:
                if (!_applying && _favoriteCycleTask.IsCompleted)
                    _favoriteCycleTask = CycleFavoriteAsync(action == TrayHotkeyAction.NextFavorite ? 1 : -1);
                break;
            case TrayHotkeyAction.ReleaseMaster:
                if (_applying || _settings.SelectedSourceKey is null) break;
                Change(() =>
                {
                    _settings.SelectedSourceKey = _settings.SelectedSourceDisplayName = _settings.LastKnownSourceKey = null;
                    _settings.LastKnownSampleRateHz = null;
                    _settings.SelectionRevision++;
                    InvalidateObservation();
                });
                break;
            case TrayHotkeyAction.ToggleFullscreenPriority:
                SetFullscreenPriority(!_settings.FullscreenAutoSwitchEnabled);
                break;
        }
    }

    private async Task CycleFavoriteAsync(int direction)
    {
        var endpoint = _settings.TargetEndpointId;
        var epoch = _epoch;
        var hotkeyGeneration = _hotkeyGeneration;
        if (endpoint is null) return;
        try
        {
            // Keypresses get a fresh census instead of selecting a source from the last
            // hidden-window poll. Favorite placeholders never become live candidates.
                using var enumerator = new AudioSessionEnumerator();
            var sources = await Task.Run(() => enumerator.Enumerate(endpoint));
            if (_exiting || _applying || epoch != _epoch || hotkeyGeneration != _hotkeyGeneration || !_settings.GlobalHotkeysEnabled
                || !StringComparer.OrdinalIgnoreCase.Equals(endpoint, _settings.TargetEndpointId)) return;
            if (enumerator.LastError is not null)
            {
                _popup.SetHotkeyStatus("Favorite switch skipped: audio sessions changed. Try again.");
                return;
            }
            var key = FavoriteCyclePolicy.Next(sources.Where(source => source.CanPersist).Select(source => source.ToSnapshot()),
                _settings, direction);
            var selected = sources.FirstOrDefault(source => StringComparer.OrdinalIgnoreCase.Equals(source.Identity, key));
            if (selected is null || StringComparer.OrdinalIgnoreCase.Equals(key, _settings.SelectedSourceKey)) return;
            Change(() => { _settings.SelectMaster(selected.Identity, selected.ProcessName); InvalidateObservation(); });
        }
        catch (Exception error)
        {
            Log("Favorite hotkey failed: " + error.Message);
            if (!_exiting) _popup.SetHotkeyStatus("Favorite switch failed; see switcher.log or try again.");
        }
    }

    private void SelectOutput(string? id) => Change(() =>
    {
        _chooseInitialOutput = false;
        _settings.TargetEndpointId = id;
        _targetWasAvailable = false;
        _reprobe = true;
        InvalidateObservation();
    });

    private void Change(Action mutation)
    {
        if (_exiting || _suspended || _applying || _diagnosticsMode) return;
        if (!_automaticMasterChange) _priorityHandoffAfterFullscreen = false;
        var previousEndpoint = _settings.TargetEndpointId;
        var previousSource = _settings.SelectedSourceKey;
        var previousRevision = _settings.SelectionRevision;
        mutation();
        _fullscreenMonitor.SetEnabled(_settings.FullscreenAutoSwitchEnabled && _settings.SwitchingEnabled);
        if (!StringComparer.OrdinalIgnoreCase.Equals(previousEndpoint, _settings.TargetEndpointId)
            || !StringComparer.OrdinalIgnoreCase.Equals(previousSource, _settings.SelectedSourceKey)
            || previousRevision != _settings.SelectionRevision)
        {
            RecordSessionContinuity(_sessionContinuity.Cancel());
            if (!_automaticMasterChange)
                _fullscreenSuppressedWindow = _fullscreenMonitor.CurrentFullscreenWindow;
        }
        _lastDecision = null;
        Volatile.Write(ref _notificationEndpoint, _settings.TargetEndpointId);
        if (!StringComparer.OrdinalIgnoreCase.Equals(previousEndpoint, _settings.TargetEndpointId)) StopMeterPolling();
        _epoch++;
        InvalidateAppleSwitchPreferences();
        _candidateKey = _failedKey = null;
        _stabilityTimer.Stop();
        _favoriteAutoTimer.Stop();
        _saveTask = SaveAsync();
        _status = "Selection saved; checking the output…";
        Render();
        StartRefresh();
        RequestMixerRead();
    }

    private void TrayThemeChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs args)
    {
        if (_exiting || _popup.IsDisposed || Interlocked.Exchange(ref _trayThemePosted, 1) != 0) return;
        try
        {
            _popup.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _trayThemePosted, 0);
                if (_exiting) return;
                var next = TrayIconArtwork.CreateForCurrentTheme();
                var previous = _trayArtwork;
                _icon.Icon = next;
                _trayArtwork = next;
                previous?.Dispose();
            }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _trayThemePosted, 0); }
    }

    private void InvalidateObservation()
    {
        ProjectAppleCatalogObservation();
        _browserObservation = null;
        _evidence = "Native rate: unknown. Set a per-app override to choose a rate.";
    }

    private async Task SaveAsync()
    {
        if (_diagnosticsMode) return;
        try
        {
            await _store.SaveAsync(_settings);
            _saveError = null;
        }
        catch (Exception ex)
        {
            _saveError = "Settings could not be saved: " + ex.Message;
            Log(_saveError);
        }
    }

    private async Task SaveFavoritesAsync()
    {
        await SaveAsync();
        if (!_exiting && !_popup.IsDisposed) Render();
    }

    // ETW and COM callbacks only post a coalesced request. Keep their threads free and
    // retain requests arriving during enumeration, a format write, or settings I/O.
    private void RequestRefresh(bool census = false)
    {
        if (_exiting || _suspended || _popup.IsDisposed) return;
        if (census) Interlocked.Exchange(ref _postedCensus, 1);
        if (Interlocked.Exchange(ref _postedRefresh, 1) != 0) return;
        try
        {
            _popup.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _postedRefresh, 0);
                if (_exiting || _suspended) return;
                _refreshPending = true;
                _censusPending |= Interlocked.Exchange(ref _postedCensus, 0) != 0;
                if (!_refreshing && !_notificationTimer.Enabled) _notificationTimer.Start();
            }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _postedRefresh, 0); }
    }

    private void StartRefresh(bool census = true)
    {
        if (_exiting || _suspended) return;
        if (Interlocked.Exchange(ref _deviceNotificationPosted, 0) != 0
            && Interlocked.Exchange(ref _pinnedDeviceResetPending, 0) != 0)
        {
            _epoch++;
            _reprobe = true;
            _targetWasAvailable = false;
            _watchedEndpointId = null;
            _failedKey = _candidateKey = null;
            _streamMonitor?.InvalidateExistingStreams();
            StopMeterPolling();
            Log("Pinned output state or properties changed; refreshing capabilities and session subscriptions.");
        }
        _refreshPending = true;
        _censusPending |= census;
        if (_refreshing) return;
        _notificationTimer.Stop();
        var captureSources = _censusPending || _reprobe || !_targetWasAvailable;
        _refreshPending = _censusPending = false;
        _refreshTask = RefreshAsync(captureSources);
    }

    private void SessionChanged()
    {
        if (_exiting || _suspended) return;
        _streamMonitor?.InvalidateExistingStreams();
        RequestTraceFlush();
        RequestRefresh(census: true);
        try
        {
            _popup.BeginInvoke(new Action(() =>
            {
                if (_exiting) return;
                _traceFlushPass = 0;
                _traceFlushTimer.Interval = 40;
                _traceFlushTimer.Start();
                RequestMixerRead();
            }));
        }
        catch (InvalidOperationException) { }
    }

    private void StreamChanged() => RequestRefresh();

    private void PlaybackChanged(object? sender, EventArgs args)
    {
        if (_exiting || _popup.IsDisposed || Interlocked.Exchange(ref _playbackNotificationPosted, 1) != 0) return;
        try
        {
            _popup.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _playbackNotificationPosted, 0);
                if (_exiting) return;
                _mediaPlayback = _playback.Snapshot.Resolve(_snapshot.Sources);
                // Paint the media pause immediately, even while a native census or
                // readback is in flight. The underlying Windows activity stays intact.
                Render();
                RequestRefresh(census: true);
            }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _playbackNotificationPosted, 0); }
    }

    private void SetFullscreenPriority(bool enabled)
    {
        if (_exiting || _suspended || _applying || _diagnosticsMode) return;
        if (enabled) _fullscreenSuppressedWindow = 0;
        Change(() => _settings.FullscreenAutoSwitchEnabled = enabled);
        Log("Fullscreen app priority " + (enabled ? "enabled." : "disabled."), "Fullscreen");
    }

    private void FullscreenChanged()
    {
        if (!_exiting && !_diagnosticsMode) RequestRefresh(census: true);
    }

    private bool TrySwitchFullscreen(Snapshot snapshot)
    {
        if (_diagnosticsMode || _applying || _saveError is not null) return false;
        // A failed census is not evidence that the fullscreen source stopped.
        if (_settings.FullscreenAutoSwitchEnabled && _settings.SwitchingEnabled
            && (!snapshot.TargetAvailable || snapshot.Error is not null)) return false;
        if (_fullscreenSuppressedWindow != _fullscreenMonitor.CurrentFullscreenWindow)
            _fullscreenSuppressedWindow = 0;
        var candidate = _fullscreenMonitor.Resolve(snapshot.Sources, _fullscreenRetainedSourceKey);
        var hadFullscreenPriority = _fullscreenPolicy.HasPriority;
        var selection = _fullscreenPolicy.Update(_settings.FullscreenAutoSwitchEnabled && _fullscreenSuppressedWindow == 0,
            _settings, candidate);
        _fullscreenStatus = !_settings.FullscreenAutoSwitchEnabled ? "Fullscreen app priority is off."
            : !_settings.SwitchingEnabled ? "Fullscreen app priority waits while switching is disabled."
            : _fullscreenSuppressedWindow != 0 ? "Manual master choice retained until you leave this fullscreen app."
            : _fullscreenMonitor.Error ?? (_fullscreenPolicy.HasPriority
                ? "Fullscreen master: " + candidate?.DisplayName
                : "Watching for a fullscreen app with active audio; manual master choices take precedence.");
        if (selection is null)
        {
            if (!_fullscreenPolicy.HasPriority) _fullscreenRetainedSourceKey = null;
            return false;
        }
        ChangeMasterAutomatically(() => ApplyFullscreenSelection(selection));
        _fullscreenPolicy.ConfirmSelection(_settings);
        _fullscreenRetainedSourceKey = _fullscreenPolicy.HasPriority ? candidate?.SourceKey : null;
        if (hadFullscreenPriority && !_fullscreenPolicy.HasPriority) _priorityHandoffAfterFullscreen = true;
        Log("Fullscreen priority selected " + (_settings.SelectedSourceDisplayName ?? "no master") + ".", "Fullscreen");
        return true;
    }

    private void ChangeMasterAutomatically(Action mutation)
    {
        _automaticMasterChange = true;
        try { Change(mutation); }
        finally { _automaticMasterChange = false; }
    }

    private void ApplyFullscreenSelection(FullscreenMasterSelection selection)
    {
        if (selection.SourceKey is { } key) _settings.SelectMaster(key, selection.DisplayName!);
        else
        {
            _settings.SelectedSourceKey = _settings.SelectedSourceDisplayName = null;
            _settings.SelectionRevision++;
        }
        _settings.LastKnownSourceKey = selection.HeldSourceKey;
        _settings.LastKnownSampleRateHz = selection.HeldRateHz;
        InvalidateObservation();
    }

    private bool TrySwitchFavorite(Snapshot snapshot, FavoritePlaybackSnapshot playbackSnapshot)
    {
        _favoriteAutoTimer.Stop();
        if (_fullscreenPolicy.HasPriority)
        {
            _favoriteAutoStatus = "Favorites wait while the fullscreen app has priority.";
            return false;
        }
        if (_diagnosticsMode || !snapshot.TargetAvailable || snapshot.Error is not null || _saveError is not null) return false;
        var decision = _favoriteAutoPolicy.Update(_settings,
            snapshot.Sources.Where(source => source.CanPersist).Select(source => source.ToSnapshot()).ToArray(),
            playbackSnapshot.ResolveForFavorites(snapshot.Sources), System.Diagnostics.Stopwatch.GetElapsedTime(_startedAt),
            _priorityHandoffAfterFullscreen);
        _favoriteAutoStatus = decision.Detail;
        if (decision.RetryAfter is { } delay)
        {
            _favoriteAutoTimer.Interval = (int)Math.Clamp(Math.Ceiling(delay.TotalMilliseconds), 50, 10000);
            _favoriteAutoTimer.Start();
        }
        if (decision.TargetSourceKey is not { } key)
        {
            // Avoid applying an intermediate restored source's format during fullscreen
            // exit while the higher-priority handoff is being confirmed.
            if (_favoriteAutoPolicy.HasPendingHandoff) _status = decision.Detail;
            return _favoriteAutoPolicy.HasPendingHandoff;
        }
        var next = snapshot.Sources.FirstOrDefault(source => source.CanPersist && source.IsActive
            && StringComparer.OrdinalIgnoreCase.Equals(source.Identity, key));
        if (next is null) return false;
        Log($"Automatic priority master: {_settings.SelectedSourceDisplayName} → {next.ProcessName}. {decision.Detail}");
        ChangeMasterAutomatically(() => { _settings.SelectMaster(next.Identity, next.ProcessName); InvalidateObservation(); });
        _priorityHandoffAfterFullscreen = false;
        return true;
    }

    private async Task ConfigureBrowserAsync()
    {
        try
        {
            if (_settings.BrowserRateDetectionEnabled && !_exiting)
            {
                if (_browserBridge is null)
                {
                    _browserBridge = new BrowserRateBridge(Path.Combine(AppContext.BaseDirectory, "browser-helper", "WindowsSourceSwitcher.BrowserHost.exe"));
                    _browserBridge.Changed += BrowserChanged;
                }
                await _browserBridge.StartAsync();
            }
            else if (_browserBridge is not null) await _browserBridge.StopAsync();
            if (!_exiting) _popup.SetBrowserStatus((_browserBridge?.Snapshot ?? BrowserDecoderSnapshot.Empty).Detail);
        }
        catch (Exception error) { if (!_exiting) _popup.SetBrowserStatus("Browser connection unavailable: " + error.Message); }
    }

    private void BrowserChanged()
    {
        if (_exiting || Interlocked.Exchange(ref _browserNotificationPosted, 1) != 0) return;
        try
        {
            _popup.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _browserNotificationPosted, 0);
                if (_exiting) return;
                _epoch++;
                _browserObservation = null;
                _popup.SetBrowserStatus((_browserBridge?.Snapshot ?? BrowserDecoderSnapshot.Empty).Detail);
                RequestRefresh();
            }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _browserNotificationPosted, 0); }
    }

    private void RequestTraceFlush()
    {
        if (_exiting || _suspended) return;
        if (_exiting || Interlocked.Exchange(ref _flushInFlight, 1) != 0) return;
        _flushTask = Task.Run(() =>
        {
            try { Volatile.Read(ref _streamMonitor)?.FlushPendingEvents(); }
            finally { Interlocked.Exchange(ref _flushInFlight, 0); }
        });
    }

    private async Task RefreshAsync(bool captureSources)
    {
        _refreshing = true;
        try
        {
            if (_powerResumePending)
            {
                _powerResumePending = false;
                if (_deviceWatcher is { } oldDeviceWatcher)
                {
                    _deviceWatcher = null;
                    oldDeviceWatcher.Changed -= DeviceChanged;
                    await Task.Run(oldDeviceWatcher.Dispose);
                }
                if (_exiting || _suspended) return;
            }
            EnsureDeviceWatcher();
            var epoch = _epoch;
            var endpointId = _settings.TargetEndpointId;
            var force = _reprobe || !_targetWasAvailable;
            _reprobe = false;
            // Subscribe to formats before the potentially expensive first device probe.
            await EnsureStreamMonitorAsync();
            await _browserConfigurationTask;
            await _playbackStartup;
            if (_exiting || epoch != _epoch) return;
            var previous = _snapshot;
            Snapshot snapshot;
            var censusStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            try { snapshot = await Task.Run(() => Capture(endpointId, force, captureSources, previous)); }
            catch
            {
                if (!_exiting && epoch == _epoch)
                    RecordOutputConnection(_outputConnection.Observe(endpointId, null,
                        System.Diagnostics.Stopwatch.GetElapsedTime(_startedAt)));
                throw;
            }
            if (_exiting || epoch != _epoch) return;
            var censusMs = System.Diagnostics.Stopwatch.GetElapsedTime(censusStarted).TotalMilliseconds;
            if (censusMs >= 250) Log($"Audio refresh timing: device/session census {censusMs:0} ms (capabilities forced={force}).", "Timing");
            _snapshot = snapshot;
            if (_chooseInitialOutput && captureSources && endpointId is null && snapshot.DeviceCensusError is null)
            {
                var defaults = snapshot.Devices.Where(device => device.IsDefault).Take(2).ToArray();
                if (defaults.Length == 1)
                {
                    SelectOutput(defaults[0].Id);
                    return;
                }
            }
            _powerStatus = "Ready; fresh audio census after startup or resume.";
            _mediaPlayback = _playback.Snapshot.Resolve(snapshot.Sources);
            if (captureSources)
            {
                RecordOutputConnection(_outputConnection.Observe(endpointId,
                    PinnedOutputConnection.AvailabilityFromCensus(snapshot.TargetAvailable, snapshot.DeviceCensusError),
                    System.Diagnostics.Stopwatch.GetElapsedTime(_startedAt)));
                RecordSessionChanges(previous, snapshot);
                // Reuse only a fresh session census. Cached rows, peaks and silence cannot
                // establish that a player reopened a stream or is audibly producing audio.
                RecordSessionContinuity(_sessionContinuity.Observe(endpointId, _settings.SelectedSourceKey,
                    _settings.SelectionRevision, snapshot.TargetAvailable, snapshot.Error is null,
                    snapshot.Sources.Any(value => value.IsActive
                        && StringComparer.OrdinalIgnoreCase.Equals(value.Identity, _settings.SelectedSourceKey)),
                    System.Diagnostics.Stopwatch.GetElapsedTime(_startedAt)));
            }
            if (!_targetWasAvailable && snapshot.TargetAvailable) _failedKey = null;
            _targetWasAvailable = snapshot.TargetAvailable;
            if (_watchedEndpointId != (snapshot.TargetAvailable ? endpointId : null)
                || (_sessionWatcher is not null && !snapshot.TargetAvailable)
                || (captureSources && _sessionWatcher?.LastError is not null && DateTimeOffset.UtcNow >= _watcherRetryAfter))
            {
                if (_sessionWatcher is not null)
                {
                    _sessionWatcher.Changed -= SessionChanged;
                    _sessionWatcher.VolumeChanged -= MixerVolumeChanged;
                    var oldWatcher = _sessionWatcher;
                    _sessionWatcher = null;
                    await Task.Run(oldWatcher.Dispose);
                }
                _watchedEndpointId = snapshot.TargetAvailable ? endpointId : null;
                if (_watchedEndpointId is not null && !_diagnosticsMode)
                {
                    _watcherRetryAfter = DateTimeOffset.UtcNow.AddSeconds(8);
                    _sessionWatcher = new AudioSessionWatcher(_watchedEndpointId);
                    _sessionWatcher.Changed += SessionChanged;
                    _sessionWatcher.VolumeChanged += MixerVolumeChanged;
                    SessionChanged();
                }
            }
            await UpdateMonitorAsync();
            if (_streamMonitor is { } streamMonitor)
            {
                // A full session census also revalidates service state. In particular,
                // the final stability check must not apply an earlier rundown's rate.
                if (captureSources) streamMonitor.InvalidateExistingStreams();
                var streamRefreshStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                await Task.Run(streamMonitor.RefreshExistingStreams);
                var streamRefreshMs = System.Diagnostics.Stopwatch.GetElapsedTime(streamRefreshStarted).TotalMilliseconds;
                if (streamRefreshMs >= 250) Log($"Audio refresh timing: existing-stream snapshot {streamRefreshMs:0} ms.", "Timing");
            }
            if (_exiting || epoch != _epoch) return;
            var observedStreamVersion = _streamMonitor?.Version ?? -1;
            UpdateStreamObservations();
            await _saveTask;
            if (_exiting || epoch != _epoch) return;

            var playbackSnapshot = _playback.Snapshot;
            _mediaPlayback = playbackSnapshot.Resolve(snapshot.Sources);
            if (!_diagnosticsMode && !_playback.IsCurrent(playbackSnapshot.Version))
            {
                RequestRefresh(census: true);
                return;
            }
            if (TrySwitchFullscreen(snapshot)) return;
            if (TrySwitchFavorite(snapshot, playbackSnapshot)) return;

            var browser = _browserBridge?.Snapshot ?? BrowserDecoderSnapshot.Empty;
            var playbackVersion = playbackSnapshot.Version;
            var selectedKey = _settings.SelectedSourceKey;
            var selectedRevision = _settings.SelectionRevision;
            var catalogGeneration = _observationGeneration;
            var catalogSession = _observation?.SessionToken;
            var sources = snapshot.Sources.Where(s => s.CanPersist).Select(s => PlaybackPresentation.ForRatePolicy(s, _mediaPlayback)).ToArray();
            _browserObservation = BrowserSourcePolicy.Observe(_settings, sources, browser,
                _browserBridge?.IsEvidenceCurrent(browser.Version) == true);
            var observations = _streamObservations.Values.Concat(new[] { _observation, _browserObservation }.OfType<RateObservation>()).ToArray();
            var decision = BrowserSourcePolicy.HoldIfRequired(_settings, browser, _browserObservation,
                SampleRatePolicy.Evaluate(_settings, sources, observations));
            _lastDecision = decision;
            if (decision.Reason == RateDecisionReason.WasapiStream
                && _streamMonitor?.IsEvidenceCurrent(observedStreamVersion) != true)
            {
                _streamObservations = new Dictionary<string, RateObservation>();
                _refreshPending = _censusPending = true;
                _status = "Audio streams changed; checking fresh formats…";
                return;
            }
            _status = decision.Detail;
            if (_settings.StreamRateDetectionEnabled && decision.Reason == RateDecisionReason.NativeRateUnknown
                && !(_settings.CatalogRateDetectionEnabled && snapshot.Sources.Any(source => source.Identity == _settings.SelectedSourceKey && source.SupportsAutomaticRate)))
                _status = _streamDetail ?? "No current shared PCM format was confirmed. Refresh / retry checks existing streams.";
            if (!_settings.CatalogRateDetectionEnabled)
                _evidence = "Native rate: unknown. Windows sessions do not report content rates.";
            if (_settings.SelectedSourceKey is { } selected && _settings.ManualSampleRatesHz.TryGetValue(selected, out var manual))
                _evidence = $"Manual override: {SourcePopup.FormatRate(manual)}. This is a user setting, not a detected rate.";

            if (_saveError is not null) { _status = _saveError; return; }
            if (_diagnosticsMode) return;
            if (endpointId is null)
            {
                _status = snapshot.DeviceCensusError ?? (snapshot.Devices.Count == 0
                    ? "No active playback device found. Connect or enable an output in Windows, then refresh."
                    : "Choose the output your apps are playing through.");
                return;
            }
            if (!snapshot.TargetAvailable && snapshot.DeviceCensusError is not null)
            { _status = "Output read failed; waiting for a fresh device check. No other output is changed."; return; }
            if (!snapshot.TargetAvailable) { _status = "Saved output disconnected. Waiting for that device; no other output is changed."; return; }
            if (snapshot.Error is { } error) { _status = error; return; }
            if (selectedKey is null)
            {
                _status = "Play something through this output, then select its app in Sources to follow its rate.";
                if (_streamMonitor is { IsRunning: false, CanRetryAutomatically: false, Error: { } streamError })
                    _status += " " + streamError + " Manual rates are available after you select an app.";
            }
            if (decision.DesiredSampleRateHz is not { } desired)
            {
                _candidateKey = null;
                _stabilityTimer.Stop();
                return;
            }
            if (snapshot.Current is null || snapshot.Mix is null) { _status = "Cannot read the output format. Refresh to retry."; return; }

            if (snapshot.Current.SampleRateHz == desired && snapshot.Mix.SampleRateHz == desired)
            {
                _failedKey = _candidateKey = null;
                await RememberAsync(desired);
                return;
            }

            var target = EndpointFormatPolicy.SelectExact(
                new(snapshot.Current.SampleRateHz, snapshot.Current.BitDepth, snapshot.Current.Channels),
                snapshot.Formats.Select(f => new EndpointFormat(f.SampleRateHz, f.BitDepth, f.Channels)), desired);
            if (target is null)
            {
                _status = $"{SourcePopup.FormatRate(desired)} is unsupported at the current bit depth/channel count. Output unchanged.";
                return;
            }
            var key = $"{endpointId}|{_settings.SelectedSourceKey}|{desired}|{snapshot.Current.BitDepth}|{snapshot.Current.Channels}|{_settings.SelectionRevision}";
            var appleSource = snapshot.Sources.FirstOrDefault(s => s.Identity == selectedKey && s.SupportsAutomaticRate);
            if (_failedKey == key) { _status = _appleSwitchFailureKey == key ? _appleSwitchDetail : "Switch failed. Check exclusive playback / device availability, then Refresh / retry. See switcher.log for details."; return; }
            if (_candidateKey != key)
            {
                _candidateKey = key;
                _candidateSince = System.Diagnostics.Stopwatch.GetTimestamp();
                // Apple has its own 200 ms same-track/live-signal preflight below.
                // Its evidence is revalidated immediately before the write, so an
                // additional sequential 180 ms timer only delays the same decision.
                if (appleSource is null)
                {
                    _status = "Waiting for a stable rate selection…";
                    _stabilityTimer.Interval = 180;
                    _stabilityTimer.Start();
                    return;
                }
            }
            if (appleSource is null && System.Diagnostics.Stopwatch.GetElapsedTime(_candidateSince) < TimeSpan.FromMilliseconds(180))
            {
                _stabilityTimer.Interval = 180;
                _stabilityTimer.Start();
                return;
            }
            _stabilityTimer.Stop();

            var now = System.Diagnostics.Stopwatch.GetElapsedTime(_startedAt);
            var retryAfter = _formatThrottle.RetryAfter(now);
            if (retryAfter > TimeSpan.Zero)
            {
                _status = $"Frequent format changes; holding output for {Math.Ceiling(retryAfter.TotalSeconds)} seconds. Refresh / retry resets this safeguard.";
                _stabilityTimer.Interval = (int)Math.Clamp(Math.Ceiling(retryAfter.TotalMilliseconds), 180, 10000);
                _stabilityTimer.Start();
                return;
            }
            // One operation at a time. Disable source/device edits while the uncancellable Windows
            // call is in flight; the next poll still verifies the pinned endpoint and current source.
            _applying = true;
            var formatOperation = ++_formatOperationGeneration;
            RecordSessionContinuity(_sessionContinuity.Cancel());
            _status = $"Applying {SourcePopup.FormatRate(desired)}… Apps may briefly restart their shared streams.";
            if (appleSource is not null) _status = "Checking Apple Music's live stream and confirming playback after switching…";
            Render();
            AppleSwitchResult? appleSwitch = null;
            var appleContextCurrent = appleSource is null ? null : CreateAppleSwitchContext(endpointId, selectedKey!, selectedRevision);
            var result = await Task.Run<ApplyResult?>(async () =>
            {
                if (_exiting || epoch != Volatile.Read(ref _epoch)) return null;
                if (decision.Reason != RateDecisionReason.ManualOverride && !_playback.IsCurrent(playbackVersion)) return null;
                if (_settings.BrowserRateDetectionEnabled
                    && decision.Reason is not (RateDecisionReason.ManualOverride or RateDecisionReason.VerifiedNative)
                    && (_browserBridge?.Snapshot ?? BrowserDecoderSnapshot.Empty).Version != browser.Version) return null;
                // A background Client callback can arrive after the UI's final check,
                // including while settings I/O or worker dispatch was pending.
                if (decision.Reason == RateDecisionReason.WasapiStream
                    && _streamMonitor?.IsEvidenceCurrent(observedStreamVersion) != true) return null;
                if (decision.Reason == RateDecisionReason.BrowserDecoder
                    && _browserBridge?.IsEvidenceCurrent(browser.Version) != true) return null;
                if (decision.Reason == RateDecisionReason.CatalogMetadata
                    && (selectedKey is null || catalogSession is null
                        || _monitor?.IsEvidenceCurrent(catalogGeneration, selectedKey, catalogSession, 0) != true)) return null;
                if (appleSource is null)
                    return EndpointRateApplier.Apply(new EndpointRateAdapter(_endpoint), endpointId, desired);

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_appleSwitchLifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(9));
                using var liveReader = new AppleMusicRenderProbe();
                AppleRenderObservation live = new(null, null);
                bool ContextCurrent() => appleContextCurrent!();
                float? Peak() { live = liveReader.Read(endpointId, selectedKey!); return live.Peak; }
                IAppleSwitchSession? session;
                try { session = await WindowsAppleSwitchSession.OpenAsync(deadline.Token).ConfigureAwait(false); }
                catch (Exception error)
                {
                    appleSwitch = new(null, "Apple Music session could not be opened; output held: " + error.Message, false, true);
                    return null;
                }
                ApplyResult? ApplyApple()
                {
                    if (!ContextCurrent() || epoch != Volatile.Read(ref _epoch)) return null;
                    if (decision.Reason != RateDecisionReason.ManualOverride && !_playback.IsCurrent(playbackVersion)) return null;
                    if (decision.Reason == RateDecisionReason.WasapiStream && _streamMonitor?.IsEvidenceCurrent(observedStreamVersion) != true) return null;
                    if (decision.Reason == RateDecisionReason.CatalogMetadata && (selectedKey is null || catalogSession is null
                        || _monitor?.IsEvidenceCurrent(catalogGeneration, selectedKey, catalogSession, 0) != true)) return null;
                    return EndpointRateApplier.Apply(new EndpointRateAdapter(_endpoint), endpointId, desired);
                }
                using (session as IDisposable)
                    appleSwitch = await new AppleMusicSwitchGuard().RunAsync(session, ContextCurrent, () => live.Active,
                        Peak, ApplyApple,
                        deadline.Token, () =>
                        {
                            var device = _endpoint.GetCurrentDeviceFormat(endpointId);
                            var mix = _endpoint.GetCurrentMixFormat(endpointId);
                            if (device is null || mix is null || _exiting || _popup.IsDisposed) return;
                            try
                            {
                                // The worker waits for this rare UI publication before
                                // starting recovery observation. A fast recovery cannot
                                // overtake and discard its own readback notification.
                                _popup.Invoke(new Action(() => PublishAppleFormatConfirmation(
                                    formatOperation, ContextCurrent, device, mix)));
                            }
                            catch (InvalidOperationException) { /* Closing or recreating the owned window. */ }
                        }).ConfigureAwait(false);
                return appleSwitch.Applied;
            });
            if (appleSwitch is not null)
            {
                if (_appleSwitchDetail != appleSwitch.Detail)
                {
                    _appleSwitchDetail = appleSwitch.Detail;
                    _history.Append("Apple Music", _appleSwitchDetail);
                    Log(_appleSwitchDetail);
                }
            }
            if (result is null)
            {
                if (appleSwitch is not null)
                {
                    if (!appleSwitch.RetryOnActivity) _failedKey = _appleSwitchFailureKey = key;
                    _status = _appleSwitchDetail;
                    return;
                }
                _streamObservations = new Dictionary<string, RateObservation>();
                _refreshPending = _censusPending = true;
                _status = "Audio streams changed; checking fresh formats…";
                return;
            }
            _formatThrottle.RecordAttempt(System.Diagnostics.Stopwatch.GetElapsedTime(_startedAt));
            Log($"{endpointId}: requested={desired}, success={result.Success}, {result.Detail}");
            Log(_endpoint.GetLastApplyDiagnostics(endpointId) ?? "No device write diagnostics.");
            _failedKey = result.Success ? null : key;
            _appleSwitchFailureKey = null;
            _status = result.Success ? $"Applied {SourcePopup.FormatRate(desired)}. Other shared-mode apps use Windows resampling." : "Switch failed: " + result.Detail;
            if (result.Success && appleSwitch is { PlaybackObserved: false })
                _status = $"Applied {SourcePopup.FormatRate(desired)}. " + appleSwitch.Detail;
            // Our own format write emits endpoint property notifications and can advance
            // the device epoch before readback returns. Remember the successful format
            // for the same semantic selection, so a paused/restarting stream cannot
            // immediately restore the older held rate.
            if (result.Success && !_exiting && _settings.TargetEndpointId == endpointId
                && StringComparer.OrdinalIgnoreCase.Equals(_settings.SelectedSourceKey, selectedKey)
                && _settings.SelectionRevision == selectedRevision) await RememberAsync(desired);
            if (result.Success)
            {
                if (!_exiting && selectedKey is not null && _settings.TargetEndpointId == endpointId
                    && StringComparer.OrdinalIgnoreCase.Equals(_settings.SelectedSourceKey, selectedKey)
                    && _settings.SelectionRevision == selectedRevision)
                    RecordSessionContinuity(_sessionContinuity.Begin(endpointId, selectedKey, selectedRevision,
                        desired, System.Diagnostics.Stopwatch.GetElapsedTime(_startedAt)));
                _favoriteAutoPolicy.SuspendForRecovery(System.Diagnostics.Stopwatch.GetElapsedTime(_startedAt), TimeSpan.FromSeconds(3));
                _refreshPending = _censusPending = true;
                RequestTraceFlush();
            }
        }
        catch (Exception ex)
        {
            _status = "Refresh failed: " + ex.Message;
            Log(ex.ToString());
        }
        finally
        {
            _applying = _refreshing = false;
            if (!_exiting)
            {
                Render();
                if (!_suspended && _refreshPending && !_notificationTimer.Enabled) _notificationTimer.Start();
            }
        }
    }

    internal void PublishAppleFormatConfirmation(long operation, Func<bool> contextCurrent,
        AudioFormatCandidate device, AudioFormatCandidate mix)
    {
        if (_exiting || _suspended || !_refreshing || !_applying || operation != _formatOperationGeneration || !contextCurrent()) return;
        _snapshot = _snapshot with { Current = device, Mix = mix };
        // Refresh remains serialized until observation completes, so another native
        // format write cannot overlap. User edits can cancel that observation now.
        _applying = false;
        _appleSwitchDetail = "Endpoint format confirmed; observing Apple Music playback separately.";
        _status = $"Output is {SourcePopup.FormatRate(device.SampleRateHz)}; checking Apple Music playback…";
        Log(_appleSwitchDetail, "Apple Music");
        Render();
    }

    // Capture a power generation separately from device epochs: the write's own
    // property notifications must not cancel confirmation, but suspend/resume must.
    internal Func<bool> CreateAppleSwitchContext(string endpointId, string sourceKey, long revision)
    {
        var generation = Volatile.Read(ref _powerGeneration);
        var preferences = Volatile.Read(ref _applePreferenceGeneration);
        return () => !_exiting && !_suspended && generation == Volatile.Read(ref _powerGeneration)
            && preferences == Volatile.Read(ref _applePreferenceGeneration)
            && _settings.SwitchingEnabled && _settings.TargetEndpointId == endpointId
            && _settings.SelectionRevision == revision
            && StringComparer.OrdinalIgnoreCase.Equals(_settings.SelectedSourceKey, sourceKey);
    }

    internal void InvalidateAppleSwitchPreferences() => Interlocked.Increment(ref _applePreferenceGeneration);

    private Snapshot Capture(string? endpointId, bool force, bool captureSources, Snapshot previous)
    {
        var devices = captureSources ? _endpoint.GetRenderDevices() : previous.Devices;
        var deviceError = captureSources ? _endpoint.LastDeviceEnumerationError : previous.DeviceCensusError;
        if (endpointId is null || devices.All(d => d.Id != endpointId))
            return new(devices, [], [], null, null, false, deviceError) { DeviceCensusError = deviceError };
        var sources = captureSources ? _enumerator.Enumerate(endpointId) : previous.Sources;
        return new(devices, sources, captureSources ? _endpoint.GetSupportedFormats(endpointId, force) : previous.Formats,
            _endpoint.GetCurrentDeviceFormat(endpointId), _endpoint.GetCurrentMixFormat(endpointId), true,
            captureSources ? _enumerator.LastError : previous.Error) { DeviceCensusError = deviceError };
    }

    private void EnsureDeviceWatcher()
    {
        if (_diagnosticsMode || _exiting) return;
        if (_deviceWatcher is not null && (!_deviceWatcher.Completion.IsCompleted
            || DateTimeOffset.UtcNow < _deviceWatcherRetryAfter)) return;
        if (_deviceWatcher is not null)
        {
            _deviceWatcher.Changed -= DeviceChanged;
            _deviceWatcher.Dispose();
        }
        _deviceWatcherRetryAfter = DateTimeOffset.UtcNow.AddSeconds(8);
        _deviceWatcher = new AudioDeviceWatcher();
        _deviceWatcher.Changed += DeviceChanged;
    }

    private void DeviceChanged(AudioEndpointChange change)
    {
        if (_exiting || _suspended || _popup.IsDisposed) return;
        var pinned = Volatile.Read(ref _notificationEndpoint);
        if (change.Kind != AudioEndpointChangeKind.DefaultChanged && (change.EndpointId is null
            || StringComparer.OrdinalIgnoreCase.Equals(change.EndpointId, pinned)))
            Interlocked.Exchange(ref _pinnedDeviceResetPending, 1);
        if (Interlocked.Exchange(ref _deviceNotificationPosted, 1) != 0) return;
        // Use the existing coalesced refresh boundary; a single format write may
        // emit many endpoint property callbacks within the same few milliseconds.
        RequestRefresh(census: true);
    }

    private void RecordOutputConnection(OutputConnectionUpdate? update)
    {
        if (update is not null) Log(update.Detail, "Output");
    }

    private void RecordSessionChanges(Snapshot before, Snapshot after)
    {
        if (_settings.SelectedSourceKey is not { } key) return;
        var previous = before.Sources.FirstOrDefault(source => StringComparer.OrdinalIgnoreCase.Equals(source.Identity, key));
        var current = after.Sources.FirstOrDefault(source => StringComparer.OrdinalIgnoreCase.Equals(source.Identity, key));
        if (previous is null && current is not null)
            Log($"Master session available: {current.ProcessName}. Reading fresh stream evidence.");
        else if (previous is not null && current is null)
            Log($"Master session unavailable: {previous.ProcessName}. Holding its selected rate.");
        else if (previous is not null && current is not null && previous.SessionToken != current.SessionToken)
            Log($"Master session membership changed: {current.ProcessName}. Audibility is not established by this event.");
    }

    private void MixerVolumeChanged()
    {
        if (_exiting || _popup.IsDisposed || Interlocked.Exchange(ref _mixerNotificationPosted, 1) != 0) return;
        try
        {
            _popup.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _mixerNotificationPosted, 0);
                if (!_exiting) RequestMixerRead();
            }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _mixerNotificationPosted, 0); }
    }

    private void RequestMixerRead()
    {
        if (_exiting || _suspended || _diagnosticsMode || !_popup.MixerVisible) return;
        if (_settings.TargetEndpointId is null)
        {
            StopMeterPolling();
            _popup.RenderMixer(new("", [], "Choose an output on the Sources tab first."));
            return;
        }
        _mixerReadRequested = true;
        if (!_mixerWorking && !_mixerWorkTimer.Enabled) _mixerWorkTimer.Start();
    }

    private void ReceiveMeterFrame(AudioMeterFrame frame)
    {
        if (_exiting || _suspended || _popup.IsDisposed) return;
        // A slow UI can retain at most one pending frame, rather than queueing a
        // history of readings that would make the meter lag behind actual activity.
        Interlocked.Exchange(ref _pendingMeterFrame, frame);
        if (Interlocked.Exchange(ref _meterFramePosted, 1) != 0) return;
        try
        {
            _popup.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _meterFramePosted, 0);
                var current = Interlocked.Exchange(ref _pendingMeterFrame, null);
                if (_exiting || _suspended || !_popup.MixerVisible || current is null
                    || current.Generation != _meterGeneration
                    || !StringComparer.OrdinalIgnoreCase.Equals(current.EndpointId, _settings.TargetEndpointId)) return;
                _popup.RenderMeters(current);
            }));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _pendingMeterFrame, null);
            Interlocked.Exchange(ref _meterFramePosted, 0);
        }
    }

    private void StopMeterPolling()
    {
        _meterGeneration = -1;
        Interlocked.Exchange(ref _pendingMeterFrame, null);
        _meterExpiryTimer.Stop();
        _meters?.Stop();
        if (!_popup.IsDisposed) _popup.StopMeters();
    }

    private void QueueMixerChange(VolumeMixerChange change)
    {
        if (_exiting || _suspended || _diagnosticsMode) return;
        if (!StringComparer.OrdinalIgnoreCase.Equals(change.Source.EndpointId, _settings.TargetEndpointId))
        {
            _popup.CompleteMixerWrite(change, new(false, 0, 0, "The output changed; reading its current volumes.", null));
            RequestMixerRead();
            return;
        }
        // Keep gain and mute separate: combining gestures with different session snapshots
        // could accidentally apply an earlier gain request to a newly created session.
        var key = change.Source.EndpointId + "\n" + change.Source.Identity + (change.Volume.HasValue ? "\nvolume" : "\nmute");
        _mixerChanges[key] = change;
        _mixerError = null;
        if (!_mixerWorking && !_mixerWorkTimer.Enabled) _mixerWorkTimer.Start();
    }

    private void StartMixerWork()
    {
        if (_exiting || _suspended || _diagnosticsMode || _mixerWorking) return;
        _mixerTask = RunMixerWorkAsync();
    }

    private async Task RunMixerWorkAsync()
    {
        _mixerWorking = true;
        VolumeMixerChange? inFlight = null;
        try
        {
            while (!_exiting && !_suspended && (_mixerChanges.Count > 0 || (_mixerReadRequested && _popup.MixerVisible)))
            {
                if (_mixerChanges.Count > 0)
                {
                    var pair = _mixerChanges.FirstOrDefault(pair => pair.Value.Volume.HasValue);
                    if (pair.Key is null) pair = _mixerChanges.First();
                    _mixerChanges.Remove(pair.Key);
                    var change = pair.Value;
                    if (!StringComparer.OrdinalIgnoreCase.Equals(change.Source.EndpointId, _settings.TargetEndpointId))
                    {
                        _popup.CompleteMixerWrite(change, new(false, 0, 0, "The output changed; refresh its volumes.", null));
                        continue;
                    }
                    inFlight = change;
                    var writeEpoch = _epoch;
                    var result = await Task.Run(() => _exiting || _suspended || writeEpoch != Volatile.Read(ref _epoch)
                        ? new AudioSessionVolumeWriteResult(false, 0, 0, "Power state or selection changed before the volume write.", null)
                        : change.Volume is { } volume
                            ? _volumes.SetVolume(change.Source.EndpointId, change.Source, volume)
                            : _volumes.SetMute(change.Source.EndpointId, change.Source, change.Muted!.Value));
                    if (_exiting) return;
                    _popup.CompleteMixerWrite(change, result);
                    inFlight = null;
                    if (!result.Success)
                    {
                        _mixerError = result.Error ?? "Windows could not confirm this volume change.";
                        Log("Session volume: " + _mixerError);
                    }
                    _mixerReadRequested |= _popup.MixerVisible;
                }
                else
                {
                    _mixerReadRequested = false;
                    if (_settings.TargetEndpointId is not { } endpointId) break;
                    var census = await Task.Run(() => _volumes.Read(endpointId));
                    if (_exiting || _suspended) return;
                    if (_popup.MixerVisible && StringComparer.OrdinalIgnoreCase.Equals(endpointId, _settings.TargetEndpointId))
                    {
                        _popup.RenderMixer(census);
                        if (!_diagnosticsMode)
                        {
                            _meters ??= new AudioSessionMeterMonitor(ReceiveMeterFrame);
                            _meterGeneration = _meters.SetSources(census);
                            _meterExpiryTimer.Start();
                        }
                        if (_mixerError is not null) _popup.SetMixerStatus(_mixerError);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _mixerError = "Volume controls could not be refreshed. Return to the Mixer tab to retry.";
            StopMeterPolling();
            Log("Session volume: " + ex);
            if (!_exiting)
            {
                if (inFlight is not null) _popup.CompleteMixerWrite(inFlight, new(false, 0, 0, _mixerError, null));
                _popup.SetMixerStatus(_mixerError);
            }
        }
        finally
        {
            _mixerWorking = false;
            if (!_exiting && !_suspended && (_mixerChanges.Count > 0 || (_mixerReadRequested && _popup.MixerVisible)))
                _mixerWorkTimer.Start();
        }
    }

    private async Task RememberAsync(int desired)
    {
        if (_settings.SelectedSourceKey is { } key &&
            (_settings.LastKnownSourceKey != key || _settings.LastKnownSampleRateHz != desired))
        {
            _settings.RememberAppliedRate(key, desired);
            _saveTask = SaveAsync();
            await _saveTask;
        }
    }

    private async Task UpdateMonitorAsync()
    {
        // Apple metadata follows Apple Music independently of the output master.
        // Never use another app's selection revision or mixer rate as its source format.
        var appleSources = _snapshot.Sources.Where(s => s.SupportsAutomaticRate).Take(2).ToArray();
        var source = appleSources.Length == 1 ? appleSources[0] : null;
        if (!_settings.CatalogRateDetectionEnabled || source is null)
        {
            _monitor?.SelectSource(null, 0);
            _appleCatalogObservation = null;
            _observation = null;
            if (_settings.CatalogRateDetectionEnabled)
                _evidence = "Native rate unknown. Catalog estimates are available only for Apple Music.";
            return;
        }
        if (_monitor is null)
        {
            _monitor = new AutomaticRateMonitor(new DiagnosticsLogger());
            _monitor.RateObserved += (_, observation) =>
            {
                if (_exiting || _suspended || _popup.IsDisposed) return;
                try
                {
                    // SelectSource invalidates synchronously on the UI thread, before this poll
                    // evaluates policy. Async network/track results marshal back to that thread.
                    if (!_popup.InvokeRequired) Receive(observation);
                    else _popup.BeginInvoke(new Action(() => Receive(observation)));
                }
                catch (InvalidOperationException) { /* App is shutting down. */ }
            };
            await _monitor.StartAsync(CancellationToken.None);
        }
        _monitor.SelectSource(source, 0);
        ProjectAppleCatalogObservation();
    }

    private async Task EnsureStreamMonitorAsync()
    {
        if (_diagnosticsMode) return;
        var retryRequested = _streamRetryRequested;
        _streamRetryRequested = false;
        if (retryRequested) _streamRecovery.Reset();
        if (retryRequested) _streamMonitor?.InvalidateExistingStreams();
        var stopped = _streamMonitor is { IsRunning: false };
        var automaticRetry = false;
        if (_settings.StreamRateDetectionEnabled && _streamMonitor is { } observer)
        {
            automaticRetry = _streamRecovery.ShouldRetry(observer.IsRunning, observer.CanRetryAutomatically,
                System.Diagnostics.Stopwatch.GetElapsedTime(_startedAt));
            if (stopped && !_streamFailureReported)
            {
                _streamFailureReported = true;
                Log(observer.Error ?? "Audio stream observer stopped; waiting for recovery.");
            }
        }
        if (!_settings.StreamRateDetectionEnabled || (stopped && (retryRequested || automaticRetry)))
        {
            if (_streamMonitor is not null)
            {
                var previous = _streamMonitor;
                _streamMonitor = null;
                previous.Changed -= StreamChanged;
                _streamObservations = new Dictionary<string, RateObservation>();
                _candidateKey = null;
                _stabilityTimer.Stop();
                await previous.DisposeAsync();
            }
        }
        if (!_settings.StreamRateDetectionEnabled)
        {
            _streamRecovery.Reset();
            _streamFailureReported = false;
            return;
        }
        if (_exiting) return;
        if (_streamMonitor is null)
        {
            if (automaticRetry) Log("Restarting the stopped audio stream observer; playback is not restarted.");
            _streamMonitor = new WasapiStreamRateMonitor();
            _streamMonitor.Changed += StreamChanged;
            await Task.Run(_streamMonitor.Start);
        }
        if (_streamMonitor?.IsRunning == true)
        {
            _streamRecovery.ShouldRetry(true, true, System.Diagnostics.Stopwatch.GetElapsedTime(_startedAt));
            if (_streamFailureReported) Log("Audio stream observer recovered; reading fresh stream evidence.");
            _streamFailureReported = false;
        }
    }

    private void UpdateStreamObservations()
    {
        _streamObservations = new Dictionary<string, RateObservation>();
        _streamDetail = null;
        if (_diagnosticsMode || !_settings.StreamRateDetectionEnabled || _streamMonitor is null) return;
        if (!_streamMonitor.IsRunning) { _streamDetail = _streamMonitor.Error; return; }
        if (_settings.TargetEndpointId is not { } endpointId) return;
        var current = new Dictionary<string, RateObservation>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in _snapshot.Sources.Where(s => s.CanPersist && s.IsActive
            && (_popup.Visible || s.Identity == _settings.SelectedSourceKey)))
        {
            var result = _streamMonitor.Resolve(endpointId, source.ProcessIds, source.ExecutablePath!, source.SessionInstanceIds, source.ProcessExecutablePaths);
            if (source.Identity == _settings.SelectedSourceKey) _streamDetail = result.Detail;
            if (result.SampleRateHz is { } rate)
                current[source.Identity] = new(source.Identity, source.SessionToken, _settings.SelectionRevision,
                    rate, RateProvenance.WasapiStream, "Windows ETW observed the application's submitted shared PCM format. Original media may differ.");
        }
        _streamObservations = current;
    }

    private void Receive(NativeRateObservation observation)
    {
        if (_exiting || !_settings.CatalogRateDetectionEnabled || observation.Generation < _observationGeneration
            || observation.SelectionRevision != 0
            || !_snapshot.Sources.Any(s => s.SupportsAutomaticRate && s.Identity == observation.SourceIdentity && s.SessionToken == observation.SessionToken)) return;
        _observationGeneration = observation.Generation;
        _appleCatalogObservation = observation;
        ProjectAppleCatalogObservation();
        if (StringComparer.OrdinalIgnoreCase.Equals(observation.SourceIdentity, _settings.SelectedSourceKey))
            _evidence = observation.SampleRateHz is { } rate ? $"Catalog estimate: {SourcePopup.FormatRate(rate)}. Highest asset rate; actual playback may differ." : observation.Evidence;
        Log(observation.Evidence);
        RequestRefresh();
    }

    private void ProjectAppleCatalogObservation()
    {
        var raw = _appleCatalogObservation;
        _observation = _settings.CatalogRateDetectionEnabled && raw is not null
            && _monitor?.IsMetadataCurrent(raw.Generation, raw.SourceIdentity, raw.SessionToken, 0) == true
            ? new(raw.SourceIdentity, raw.SessionToken, _settings.SelectionRevision, raw.SampleRateHz,
                raw.SampleRateHz is null ? RateProvenance.Unknown : RateProvenance.CatalogMetadata, raw.Evidence)
            : null;
    }

    private async Task RetryCatalogAsync()
    {
        try { if (_monitor is not null) await _monitor.ResolveCurrentAsync(CancellationToken.None); }
        catch (Exception ex) { Log(ex.ToString()); }
    }

    private void Render()
    {
        ProjectAppleCatalogObservation();
        // Keep the control loop and tooltip current while hidden, without rebuilding
        // evidence strings, row signatures, and WinForms controls for an invisible popup.
        if (_popup.Visible || !_popup.IsHandleCreated)
        {
            var status = _saveError ?? (_warning is null ? _status : _warning);
            var source = _snapshot.Sources.FirstOrDefault(s => s.Identity == _settings.SelectedSourceKey);
            var evidence = _evidence;
            if (source is not null)
            {
                _streamObservations.TryGetValue(source.Identity, out var streamObservation);
                var display = SourceRatePresentation.Describe(source, _settings, _browserObservation ?? _observation, streamObservation,
                    _browserBridge?.Snapshot, _mediaPlayback.GetValueOrDefault(source.Identity));
                // Show capability immediately after selection, before the next census. A saved
                // Apple Music preference never implies a detector exists for another app.
                if (_settings.StreamRateDetectionEnabled || !source.SupportsAutomaticRate || !_settings.CatalogRateDetectionEnabled
                    || _settings.ManualSampleRatesHz.ContainsKey(source.Identity)
                    || display.Evidence is not null)
                    evidence = display.Detail;
                if (_settings.StreamRateDetectionEnabled && !_settings.ManualSampleRatesHz.ContainsKey(source.Identity)
                    && streamObservation is null && _streamMonitor?.Error is { } error)
                    evidence = error;
            }
            else if (_settings.SelectedSourceKey is { } missingSource)
            {
                evidence = _settings.ManualSampleRatesHz.TryGetValue(missingSource, out var manual)
                    ? $"Selected app is unavailable. Its saved manual target is {SourcePopup.FormatRate(manual)}."
                    : StringComparer.OrdinalIgnoreCase.Equals(_settings.LastKnownSourceKey, missingSource)
                        && _settings.LastKnownSampleRateHz is { } held
                        ? $"Selected app is unavailable. Holding its confirmed {SourcePopup.FormatRate(held)} output rate."
                        : "Selected app is unavailable. Output stays unchanged while waiting for its stream.";
            }
            _popup.Render(_snapshot.Devices, _snapshot.Sources, _snapshot.Formats, _snapshot.Current,
                _settings, _suspended ? _powerStatus : status, evidence, _applying || _suspended, _browserObservation ?? _observation, _streamObservations, _browserBridge?.Snapshot, _mediaPlayback, _observation);
            _popup.SetFavoriteAutoSwitchStatus(_favoriteAutoStatus);
            _popup.SetFullscreenStatus(_fullscreenStatus);
            if (_popup.DiagnosticsVisible) RenderDiagnostics();
        }
        var tooltip = _snapshot.Current is null ? Program.ProductName : $"{Program.ProductName} · {SourcePopup.FormatRate(_snapshot.Current.SampleRateHz)}";
        _icon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
    }

    private void RecordSessionContinuity(SessionContinuityUpdate? update)
    {
        if (update is not null) Log(update.Detail, "Session check");
    }

    private void Log(string message, string kind = "Controller")
    {
        _history.Append(kind, message);
        if (_diagnosticsMode) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 2_000_000)
                File.Move(_logPath, _logPath + ".previous", overwrite: true);
            File.AppendAllText(_logPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void RenderDiagnostics()
    {
        var source = _snapshot.Sources.FirstOrDefault(value => StringComparer.OrdinalIgnoreCase.Equals(value.Identity, _settings.SelectedSourceKey));
        _streamObservations.TryGetValue(_settings.SelectedSourceKey ?? "", out var stream);
        var streamRate = source is not null && stream is not null && stream.SessionToken == source.SessionToken
            && stream.SelectionRevision == _settings.SelectionRevision ? stream.SampleRateHz : null;
        var target = source is null ? "No current audio session"
            : SourceRatePresentation.Describe(source, _settings, _browserObservation ?? _observation, stream, _browserBridge?.Snapshot,
                _mediaPlayback.GetValueOrDefault(source.Identity)).Text;
        var browser = _browserBridge?.Snapshot ?? BrowserDecoderSnapshot.Empty;
        var confidence = _lastDecision?.Reason switch
        {
            RateDecisionReason.ManualOverride => "Manual target (not a measurement)",
            RateDecisionReason.WasapiStream => "Observed submitted PCM; original media unverified",
            RateDecisionReason.BrowserDecoder => browser.EvidenceField == BrowserWireProtocol.ChromiumHeaderEvidenceField
                ? "Chosen tab media file header; tab routing and browser processing unverified"
                : browser.EvidenceField == BrowserWireProtocol.ChromiumStreamEvidenceField
                ? "Chosen tab stream initialization header; tab routing and browser processing unverified"
                : "Chosen tab decoder metadata; tab routing and later processing unverified",
            RateDecisionReason.BrowserHold => "Held browser target; fresh decoder evidence unavailable",
            RateDecisionReason.VerifiedNative => "Verified source adapter",
            RateDecisionReason.CatalogMetadata => "Catalog estimate; stream unverified",
            RateDecisionReason.SourcePaused or RateDecisionReason.SourceUnavailable => "Held target; no fresh source measurement",
            RateDecisionReason.ConflictingObservations => "Conflicting evidence; holding output",
            _ => "Unknown / no current automatic target"
        };
        static string Format(AudioFormatCandidate? value) => value is null ? "Unavailable"
            : $"{SourcePopup.FormatRate(value.SampleRateHz)} · {value.BitDepth}-bit · {value.Channels} channels";
        var resampling = streamRate is null || _snapshot.Mix is null ? "Unknown: submitted PCM or mixer format unavailable"
            : streamRate != _snapshot.Mix.SampleRateHz ? "Expected: submitted PCM and Windows mixer rates differ"
            : "No submitted-PCM/mixer rate mismatch; original media conversion and DSP remain possible";
        var rates = _snapshot.Formats.Where(format => _snapshot.Current is { } current
                && format.BitDepth == current.BitDepth && format.Channels == current.Channels)
            .Select(format => format.SampleRateHz).Distinct().Order().Select(SourcePopup.FormatRate);
        _popup.RenderDiagnostics(new([
            new("Output", _snapshot.Devices.FirstOrDefault(value => value.Id == _settings.TargetEndpointId)?.FriendlyName ?? "Saved output unavailable"),
            new("Output connection", _outputConnection.Detail),
            new("Power recovery", _powerStatus),
            new("Apple Music switching", _appleSwitchDetail),
            new("Master", _settings.SelectedSourceDisplayName ?? "None"),
            new("Media playback", source is null ? "No session" : _mediaPlayback.GetValueOrDefault(source.Identity).ToString()),
            new("Windows session", source is null ? "No session" : source.IsActive ? "Active" : "Idle"),
            new("Automatic favorites", _favoriteAutoStatus),
            new("Fullscreen priority", _fullscreenStatus),
            new("Target / evidence", target),
            new("Confidence", confidence),
            new("Original media", _browserObservation?.SampleRateHz is { } decoded ? SourcePopup.FormatRate(decoded) + " chosen tab decoder; app routing unverified" : "Unknown; submitted PCM does not establish content rate"),
            new("Submitted PCM", streamRate is { } rate ? SourcePopup.FormatRate(rate) : "Not observed"),
            new("Windows mix", Format(_snapshot.Mix)),
            new("Endpoint format", Format(_snapshot.Current)),
            new("Post-switch session", _sessionContinuity.Detail),
            new("Resampling", resampling),
            new("Supported rates", string.Join(", ", rates)),
            new("Stream detector", _streamMonitor?.Error ?? (_streamMonitor?.IsRunning == true ? "Running: Client events and brief Service snapshots" : "Off / unavailable")),
            new("Stream recovery", _settings.StreamRateDetectionEnabled ? _streamRecovery.Status : "Off"),
            new("Device notifications", _deviceWatcher?.LastError ?? (_deviceWatcher?.IsReady == true ? "Registered" : "Starting / unavailable")),
            new("Playback scope", "Shared-mode control. Exclusive/ASIO/native DSD and physical DAC output are not verified."),
            new("Last decision", _lastDecision?.Detail ?? _status),
            new("Endpoint identity", _settings.TargetEndpointId ?? "None"),
            new("Source identity", _settings.SelectedSourceKey ?? "None")
        ], _history.Entries));
    }

    internal async Task ShutdownAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _updateLifetime.Cancel();
        _tutorial?.Dispose();
        _tutorial = null;
        _appleSwitchLifetime.Cancel();
        _hotkeyGeneration++;
        _hotkeys.SetEnabled(false);
        _fullscreenMonitor.SetEnabled(false);
        _timer.Stop();
        _notificationTimer.Stop();
        _stabilityTimer.Stop();
        _favoriteAutoTimer.Stop();
        _traceFlushTimer.Stop();
        _mixerPollTimer.Stop();
        _mixerWorkTimer.Stop();
        StopMeterPolling();
        _mixerChanges.Clear();
        _popup.Hide();
        try
        {
            await _refreshTask;
            await _flushTask;
            await _mixerTask;
            await _favoriteCycleTask;
            await _playbackStartup;
            _playback.Changed -= PlaybackChanged;
            await _playback.DisposeAsync();
            await _saveTask;
            await _browserConfigurationTask;
            // Save the original preference on graceful exit, without touching endpoint format.
            if (_fullscreenPolicy.Update(false, _settings, null) is { } restoredMaster)
            {
                ApplyFullscreenSelection(restoredMaster);
                _fullscreenPolicy.ConfirmSelection(_settings);
                await SaveAsync();
            }
            if (_browserBridge is not null) { _browserBridge.Changed -= BrowserChanged; await _browserBridge.DisposeAsync(); }
            if (_deviceWatcher is not null)
            {
                _deviceWatcher.Changed -= DeviceChanged;
                await Task.Run(_deviceWatcher.Dispose);
            }
            if (_meters is not null) await Task.Run(_meters.Dispose);
            if (_sessionWatcher is not null)
            {
                _sessionWatcher.Changed -= SessionChanged;
                _sessionWatcher.VolumeChanged -= MixerVolumeChanged;
                await Task.Run(_sessionWatcher.Dispose);
            }
            if (_monitor is not null) await _monitor.DisposeAsync();
            if (_streamMonitor is not null) { _streamMonitor.Changed -= StreamChanged; await _streamMonitor.DisposeAsync(); }
            _enumerator.Dispose();
            _volumes.Dispose();
        }
        catch (Exception ex) { Log(ex.ToString()); }
        _icon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _popup.PowerEventReceived -= HandlePowerEvent;
            _timer.Dispose();
            _appleSwitchLifetime.Dispose();
            _updateLifetime.Dispose();
            _notificationTimer.Dispose();
            _stabilityTimer.Dispose();
            _favoriteAutoTimer.Dispose();
            _traceFlushTimer.Dispose();
            _mixerPollTimer.Dispose();
            _mixerWorkTimer.Dispose();
            _meterExpiryTimer.Dispose();
            _hotkeys.Dispose();
            _fullscreenMonitor.Changed -= FullscreenChanged;
            _fullscreenMonitor.Dispose();
            if (_deviceWatcher is not null) { _deviceWatcher.Changed -= DeviceChanged; _deviceWatcher.Dispose(); }
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= TrayThemeChanged;
            _meters?.Dispose();
            _enumerator.Dispose();
            _volumes.Dispose();
            _icon.Dispose();
            _trayArtwork?.Dispose();
            _popup.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed record Snapshot(IReadOnlyList<AudioDeviceInfo> Devices, IReadOnlyList<PlatformAudioSource> Sources,
        IReadOnlyList<AudioFormatCandidate> Formats, AudioFormatCandidate? Current, AudioFormatCandidate? Mix,
        bool TargetAvailable, string? Error)
    {
        public string? DeviceCensusError { get; init; }
    }
}

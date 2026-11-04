namespace WindowsSourceSwitcher.Tray;
/// <summary>Opt-in hotkey preferences; registration and actions belong to the tray host.</summary>
internal sealed class ControlsSettingsView : UserControl
{
    private readonly TableLayoutPanel _layout;
    private readonly CheckBox _hotkeysEnabled = new MinimalCheckBox()
    {
        AutoSize = true,
        Text = "Enable global hotkeys",
        AccessibleName = "Enable global hotkeys",
        AccessibleDescription = "Enable the listed shortcuts while " + Program.ProductName + " is running. Disabled by default."
    };
    private readonly Label _hotkeyStatus = new()
    {
        AutoSize = true,
        MaximumSize = new Size(414, 0),
        Text = "Hotkeys are disabled.",
        AccessibleName = "Global hotkey status"
    };
    private readonly Button _retryHotkeys = new()
    {
        AutoSize = true,
        Text = "Retry hotkeys",
        AccessibleName = "Retry global hotkey registration",
        AccessibleDescription = "Try registering enabled shortcuts again after a shortcut conflict is resolved.",
        Enabled = false
    };
    private bool _rendering;
    private readonly int _detectionRow;
    private readonly TableLayoutPanel _detectionLayout;
    private readonly CheckBox _fullscreen = new MinimalCheckBox() { AutoSize = true,
        Text = "Fullscreen app priority", AccessibleName = "Fullscreen app priority" };
    private readonly Label _fullscreenStatus = new() { AutoSize = true, MaximumSize = new Size(414, 0),
        AccessibleName = "Fullscreen priority status", Text = "Fullscreen app priority is off." };
    private readonly CheckBox _autoFavorites = new MinimalCheckBox() { AutoSize = true,
        Text = "Auto-switch by source priority", AccessibleName = "Auto-switch by source priority" };
    private readonly Label _autoFavoriteStatus = new() { AutoSize = true, MaximumSize = new Size(414, 0),
        AccessibleName = "Automatic favorite switching status", Text = "Automatic favorite switching is off." };
    private readonly CheckBox _browserEnabled = new MinimalCheckBox() { AutoSize = true, Text = "Enable Helium tab integration" };
    private readonly Button _registerBrowser = new() { AutoSize = true, Text = "Register browser helper" };
    private readonly Label _browserStatus = new() { AutoSize = true, MaximumSize = new Size(414, 0) };
    private readonly Label _outputCapabilities = new() { AutoSize = true, MaximumSize = new Size(414, 0) };
    private readonly Label _updateStatus = new() { AutoSize = true, MaximumSize = new Size(414, 0), Text = "Ready to check for updates." };
    private readonly Button _checkUpdate = new() { AutoSize = true, Text = "Check for updates" };
    private readonly Button _installUpdate = new() { AutoSize = true, Text = "Update", Enabled = false };
    public event Action? UpdateCheckRequested;
    public event Action? UpdateInstallRequested;
    public event Action<bool>? GlobalHotkeysChanged;
    public event Action<bool>? FavoriteAutoSwitchChanged;
    public event Action<bool>? FullscreenAutoSwitchChanged;
    public event Action? HotkeyRetryRequested;
    public event Action<bool>? BrowserIntegrationChanged;
    public event Action? BrowserRegistrationRequested;
    public ControlsSettingsView()
    {
        Dock = DockStyle.Fill;
        AutoScroll = true;
        _layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            Padding = new Padding(16, 0, 16, 16), ColumnCount = 1, RowCount = 0 };
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        TableLayoutPanel? section = null;
        void Add(Control control)
        {
            var row = section!.RowCount++;
            section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            section.Controls.Add(control, 0, row);
        }
        void Section(string title)
        {
            var card = new SectionCard { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            section = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 0, Margin = Padding.Empty };
            section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            card.Controls.Add(section);
            var row = _layout.RowCount++;
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _layout.Controls.Add(card, 0, row);
            Add(new Label { AutoSize = true, Text = title, Font = new Font(Font.FontFamily, 11, FontStyle.Bold), Margin = new Padding(0, 0, 0, 12) });
        }
        void Note(string text) => Add(new Label { AutoSize = true, MaximumSize = new Size(414, 0),
            ForeColor = Color.DimGray, Text = text, Margin = new Padding(0, 5, 0, 8) });
        if (PortableUpdateInstaller.CanInstall)
        {
            Section("Updates");
            Add(_updateStatus);
            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            actions.Controls.AddRange([_checkUpdate, _installUpdate]);
            Add(actions);
            _checkUpdate.Click += (_, _) => UpdateCheckRequested?.Invoke();
            _installUpdate.Click += (_, _) => UpdateInstallRequested?.Invoke();
        }
        Section("Automatic source selection");
        Add(_autoFavorites);
        Note("Favorites → pinned → regular apps. Automatic handoffs, including Apple Music, take 0.5 s. Player recovery after an output-rate change is separate. Release stops automatic following.");
        Add(_autoFavoriteStatus);
        _fullscreen.Margin = new Padding(0, 16, 0, 4);
        Add(_fullscreen);
        Note("Prioritize any foreground fullscreen app with active audio. On exit, return to automatic priority or the previous master. Manual selection and Release take precedence.");
        Add(_fullscreenStatus);
        Section("Rate detection");
        // The actual preference controls are supplied by SourcePopup, preserving their event ownership.
        _detectionLayout = section!;
        _detectionRow = _detectionLayout.RowCount;
        _detectionLayout.RowCount += 3;
        _detectionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _detectionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _detectionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Note("Stream rates describe PCM sent to Windows. Apple Music catalog rates are estimates; original media remains unverified.");
        Note("Bit depth follows observed PCM, including 32-bit float, or an enabled Apple Music catalog estimate. Unknown depth or a rate override keeps the current depth.");
        Section("Output capabilities");
        Add(_outputCapabilities);
        Note("Highest probed PCM rate at each depth for the selected output and channel count. Playback follows the source format within these limits.");
        Section("Keyboard shortcuts");
        Add(_hotkeysEnabled);
        Note("Shortcuts work while the window is hidden.");
        var shortcuts = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            ColumnCount = 2, RowCount = 6, Margin = new Padding(0, 8, 0, 6) };
        shortcuts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 174));
        shortcuts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddShortcut(shortcuts, 0, "Ctrl + Alt + Shift + O", "Open Sources");
        AddShortcut(shortcuts, 1, "Ctrl + Alt + Shift + M", "Open Mixer");
        AddShortcut(shortcuts, 2, "Ctrl + Alt + Shift + Left", "Previous active favorite");
        AddShortcut(shortcuts, 3, "Ctrl + Alt + Shift + Right", "Next active favorite");
        AddShortcut(shortcuts, 4, "Ctrl + Alt + Shift + R", "Release master");
        AddShortcut(shortcuts, 5, "Ctrl + Alt + Shift + F", "Toggle fullscreen priority");
        Add(shortcuts);
        Note("Favorites without an active Windows audio session are skipped. Some paused apps keep their session active. Shortcut conflicts are shown below.");
        Add(_hotkeyStatus);
        _retryHotkeys.Margin = new Padding(0, 8, 0, 0);
        Add(_retryHotkeys);
        if (BrowserBridgeInstaller.IsAvailable(AppContext.BaseDirectory))
        {
            Section("Helium");
            Add(_browserEnabled);
            Add(_registerBrowser);
            Add(_browserStatus);
            Note("Load the extension from browser/chromium. Choose Helium as master in Sources, then follow a tab in the extension.");
        }
        Controls.Add(_layout);
        _hotkeysEnabled.CheckedChanged += (_, _) =>
        {
            _retryHotkeys.Enabled = _hotkeysEnabled.Checked;
            if (!_rendering) GlobalHotkeysChanged?.Invoke(_hotkeysEnabled.Checked);
        };
        _retryHotkeys.Click += (_, _) => { if (!_rendering && _retryHotkeys.Enabled) HotkeyRetryRequested?.Invoke(); };
        _autoFavorites.CheckedChanged += (_, _) => { if (!_rendering) FavoriteAutoSwitchChanged?.Invoke(_autoFavorites.Checked); };
        _fullscreen.CheckedChanged += (_, _) => { if (!_rendering) FullscreenAutoSwitchChanged?.Invoke(_fullscreen.Checked); };
        _browserEnabled.CheckedChanged += (_, _) => { if (!_rendering) BrowserIntegrationChanged?.Invoke(_browserEnabled.Checked); };
        _registerBrowser.Click += (_, _) => BrowserRegistrationRequested?.Invoke();
    }
    public void SetDetectionControls(CheckBox streamDetection, CheckBox catalog, CheckBox followBits)
    {
        _detectionLayout.Controls.Add(streamDetection, 0, _detectionRow);
        _detectionLayout.Controls.Add(catalog, 0, _detectionRow + 1);
        _detectionLayout.Controls.Add(followBits, 0, _detectionRow + 2);
    }
    public void Render(bool hotkeysEnabled, bool browserEnabled = false, bool favoriteAutoSwitchEnabled = false, bool fullscreenAutoSwitchEnabled = false)
    {
        _rendering = true;
        try
        {
            _hotkeysEnabled.Checked = hotkeysEnabled;
            _retryHotkeys.Enabled = hotkeysEnabled;
            _autoFavorites.Checked = favoriteAutoSwitchEnabled;
            _fullscreen.Checked = fullscreenAutoSwitchEnabled;
            _browserEnabled.Checked = browserEnabled;
        }
        finally { _rendering = false; }
    }
    public void SetUpdateStatus(string text, bool canCheck, bool canInstall)
    {
        _updateStatus.Text = text;
        _checkUpdate.Enabled = canCheck;
        _installUpdate.Enabled = canInstall;
    }
    public void SetHotkeyStatus(string status) => _hotkeyStatus.Text = status;
    public void SetOutputCapabilities(string capabilities) => _outputCapabilities.Text = capabilities;
    public void SetBrowserStatus(string status) => _browserStatus.Text = status;
    public void SetFavoriteAutoSwitchStatus(string status) => _autoFavoriteStatus.Text = status;
    public void SetFullscreenStatus(string status) => _fullscreenStatus.Text = status;
    private static void AddShortcut(TableLayoutPanel layout, int row, string chord, string action)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { AutoSize = true, Text = chord, Margin = new Padding(0, 4, 6, 4) }, 0, row);
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(232, 0), Text = action,
            Margin = new Padding(0, 4, 0, 4) }, 1, row);
    }
}

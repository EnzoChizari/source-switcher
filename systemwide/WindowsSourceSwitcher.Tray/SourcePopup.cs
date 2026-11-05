using WindowsLosslessSwitcher.Models;
using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;
using System.Runtime.InteropServices;

namespace WindowsSourceSwitcher.Tray;

internal sealed class SourcePopup : Form
{
    private readonly ComboBox _devices = new MinimalComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ListView _sources = new MinimalListView() { View = View.Details, FullRowSelect = true, MultiSelect = false,
        HideSelection = false, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable, Activation = ItemActivation.Standard };
    private readonly ComboBox _rates = new MinimalComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill,
        AccessibleName = "Master's target rate" };
    private readonly ComboBox _depths = new MinimalComboBox() { Dock = DockStyle.Fill, AccessibleName = "Master bit depth" };
    private readonly Label _master = new() { AutoSize = true, MaximumSize = new Size(430, 0), Text = "Master: choose an app below" };
    private readonly Label _format = new() { AutoSize = true, Text = "Output: choose a Windows playback device" };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(430, 0), Text = "Select an output, then an app playing on it." };
    private readonly Label _evidence = new() { AutoSize = true, MaximumSize = new Size(430, 0), Text = "Select an app to see its detection support and saved target." };
    private readonly CheckBox _enabled = new MinimalCheckBox() { AutoSize = true, Text = "Enable switching", Checked = true };
    private readonly CheckBox _catalog = new MinimalCheckBox() { AutoSize = true, KeepPaletteDuringFormatChange = true,
        Text = "Use Apple Music track format (catalog fallback)" };
    private readonly CheckBox _appleAutoRead = new MinimalCheckBox() { AutoSize = true, KeepPaletteDuringFormatChange = true,
        Text = "Read Apple Music Lossless details automatically" };
    private readonly CheckBox _streamDetection = new MinimalCheckBox() { AutoSize = true, KeepPaletteDuringFormatChange = true,
        Text = "Auto-detect application stream rate" };
    private readonly CheckBox _followBits = new MinimalCheckBox() { AutoSize = true, KeepPaletteDuringFormatChange = true,
        Text = "Follow source bit depth (experimental)" };
    private readonly Button _refresh = new() { AutoSize = true, Text = "Refresh / retry" };
    private readonly Button _release = new() { AutoSize = true, Text = "Release master" };
    private readonly Button _favorite = new() { AutoSize = true, Text = "☆", AccessibleName = "Favorite selected source" };
    private readonly Button _pin = new() { Text = "Pin", AccessibleName = "Pin selected source", AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(60, 30) };
    private readonly ToolTip _tips = new();
    private readonly TabControl _tabs = new MinimalTabControl() { Dock = DockStyle.Fill, NavigationOnly = true, TabStop = false };
    private readonly ImageList _sourceRowSpacing = new() { ImageSize = new Size(1, 30), ColorDepth = ColorDepth.Depth32Bit };
    private readonly Bitmap _sourceRowBitmap = new(1, 30);
    private readonly TabPage _sourcesTab = new("Sources");
    private readonly TabPage _mixerTab = new("Mixer");
    private readonly TabPage _controlsTab = new("Settings");
    private readonly TabPage _diagnosticsTab = new("Diagnostics");
    private readonly VolumeMixerView _mixer = new();
    private readonly ControlsSettingsView _controls = new();
    private readonly DiagnosticsView _diagnostics = new();
    private readonly Label _sectionTitle = new() { Dock = DockStyle.Top, Height = 52, Text = "Sources", TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(18, 0, 0, 0) };
    private readonly Panel _navigation = new() { Dock = DockStyle.Left, Width = 152, Tag = "Navigation", AccessibleName = "Audio navigation" };
    private readonly List<SidebarButton> _navigationButtons = [];
    private readonly Panel _navigationViewport = new() { Dock = DockStyle.Fill, AutoScroll = true, Tag = "Navigation" };
    private TableLayoutPanel? _navigationRail;
    private Label? _navigationVersion;
    private bool _navigationVersionHidden;
    private bool _rendering;
    private string _rowsSignature = "";
    private string _deviceSignature = "";
    private IReadOnlyList<AudioFormatCandidate>? _capabilityFormats;
    private int? _capabilityChannels;
    private int _deviceSelectionIndex = -1;
    private string _rateSignature = "";
    private string _depthSignature = "";
    private SourceListEntry? _selectedRow;
    private SourceSettings? _rowSettings;
    private bool _rowActionsLocked;

    public event Action<string>? DeviceSelected;
    public event Action<PlatformAudioSource>? SourceSelected;
    public event Action<string, string>? SavedSourceSelected;
    public event Action<string, string, bool>? FavoriteChanged;
    public event Action<string, string, bool>? PinChanged;
    public event Action<int?>? RateSelected;
    public event Action<int?>? BitDepthSelected;
    public event Action<bool>? SwitchingEnabledChanged;
    public event Action<bool>? CatalogChanged;
    public event Action<bool>? AppleAutoReadChanged;
    public event Action<bool>? FollowBitDepthChanged;
    public event Action<bool>? StreamDetectionChanged;
    public event Action<bool>? GlobalHotkeysChanged;
    public event Action<bool>? FavoriteAutoSwitchChanged;
    public event Action<bool>? FullscreenAutoSwitchChanged;
    public event Action? HotkeyRetryRequested;
    internal event Action? UpdateCheckRequested
    {
        add => _controls.UpdateCheckRequested += value;
        remove => _controls.UpdateCheckRequested -= value;
    }

    internal event Action? UpdateInstallRequested
    {
        add => _controls.UpdateInstallRequested += value;
        remove => _controls.UpdateInstallRequested -= value;
    }

    internal void SetUpdateStatus(string status, bool canCheck, bool canInstall, bool updateAvailable)
    {
        _controls.SetUpdateStatus(status, canCheck, canInstall);
        var index = _tabs.TabPages.IndexOf(_controlsTab);
        if (index >= 0 && index < _navigationButtons.Count)
            _navigationButtons[index].UpdateAvailable = updateAvailable;
    }

    public event Action<bool>? BrowserIntegrationChanged
    {
        add => _controls.BrowserIntegrationChanged += value;
        remove => _controls.BrowserIntegrationChanged -= value;
    }
    public event Action? BrowserRegistrationRequested
    {
        add => _controls.BrowserRegistrationRequested += value;
        remove => _controls.BrowserRegistrationRequested -= value;
    }
    internal event Action<int>? PowerEventReceived;
    public event Action? RetryRequested;
    public event Action? ReleaseRequested;
    public event Action? MixerVisibilityChanged;
    public event Action? DiagnosticsVisibilityChanged;
    public event Action<VolumeMixerChange>? VolumeChangeRequested;
    public bool MixerVisible => Visible && _tabs.SelectedTab == _mixerTab;
    public bool DiagnosticsVisible => Visible && _tabs.SelectedTab == _diagnosticsTab;

    public SourcePopup()
    {
        SuspendLayout();
        Text = $"{Program.ProductName} · {Program.DisplayVersion}";
        Font = new Font("Segoe UI", 9.5F);
        ClientSize = new Size(680, 740);
        FormBorderStyle = FormBorderStyle.None;
        DoubleBuffered = true;
        Padding = new Padding(1);
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        _sources.Columns.Add("App / process", 175);
        _sources.Columns.Add("Session", 110);
        _sources.Columns.Add("Rate / evidence", 150);
        // ImageList retains the original until native handle creation/recreation.
        // Keep this image alive for the complete list lifetime.
        _sourceRowSpacing.Images.Add(_sourceRowBitmap);
        _sources.SmallImageList = _sourceRowSpacing;
        _sources.ClientSizeChanged += (_, _) => FitSourceColumns();
        _sources.HandleCreated += (_, _) => FitSourceColumns(resetFixedWidths: true);
        _sources.FontChanged += (_, _) => FitSourceColumns(resetFixedWidths: true);
        _sources.DpiChangedAfterParent += (_, _) => FitSourceColumns(resetFixedWidths: true);
        _format.Font = new Font(Font.FontFamily, 13F, FontStyle.Bold);
        _master.Font = new Font(Font.FontFamily, 9.5F, FontStyle.Bold);
        _status.ForeColor = _evidence.ForeColor = Color.DimGray;
        _status.MaximumSize = _evidence.MaximumSize = new Size(440, 0);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, MinimumSize = new Size(400, 612),
            Padding = new Padding(16, 0, 16, 10), ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 184));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        SectionCard Card(int row)
        {
            var card = new SectionCard { Dock = DockStyle.Fill };
            layout.Controls.Add(card, 0, row);
            return card;
        }
        var output = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        output.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 3; row++) output.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        output.Controls.Add(new Label { Text = "Output device", AutoSize = true }, 0, 0);
        output.Controls.Add(_devices, 0, 1);
        output.Controls.Add(_format, 0, 2);
        var outputCard = Card(0);
        outputCard.AutoSize = true;
        outputCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        outputCard.Controls.Add(output);
        var apps = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        apps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        apps.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        apps.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        apps.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var masterActions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        masterActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        masterActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        masterActions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _master.AutoSize = false; _master.AutoEllipsis = true; _master.MaximumSize = Size.Empty;
        _master.Dock = DockStyle.Fill; _master.TextAlign = ContentAlignment.MiddleLeft;
        _favorite.Anchor = _pin.Anchor = AnchorStyles.Right;
        _favorite.AutoSize = false; _favorite.Size = new Size(34, 30);
        _favorite.Margin = _pin.Margin = new Padding(6, 0, 0, 0);
        masterActions.Controls.Add(_master, 0, 0);
        masterActions.Controls.Add(_favorite, 1, 0);
        masterActions.Controls.Add(_pin, 2, 0);
        apps.Controls.Add(masterActions, 0, 0); apps.Controls.Add(_sources, 0, 1);
        apps.Controls.Add(new Label { AutoSize = true, ForeColor = Color.DimGray,
            Text = "Select to pin or favorite. Double-click to set master." }, 0, 2);
        Card(1).Controls.Add(apps);
        var target = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Margin = Padding.Empty };
        target.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
        target.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        foreach (var height in new[] { 22, 32, 44, 48 }) target.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        target.Controls.Add(new Label { Text = "Master's target rate", AutoSize = true }, 0, 0);
        target.Controls.Add(new Label { Text = "Master bit depth", AutoSize = true }, 1, 0);
        target.Controls.Add(_rates, 0, 1);
        target.Controls.Add(_depths, 1, 1);
        foreach (var label in new[] { _status, _evidence }) { label.AutoSize = false; label.Dock = DockStyle.Fill; label.AutoEllipsis = true; label.MaximumSize = Size.Empty; }
        target.Controls.Add(_evidence, 0, 2); target.Controls.Add(_status, 0, 3);
        target.SetColumnSpan(_evidence, 2);
        target.SetColumnSpan(_status, 2);
        Card(2).Controls.Add(target);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        actions.Controls.AddRange([_enabled, _refresh, _release]);
        var actionCard = Card(3);
        actionCard.AutoSize = true;
        actionCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        actionCard.Padding = new Padding(12, 6, 12, 6);
        actionCard.Controls.Add(actions);
        layout.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Top, ForeColor = Color.DimGray,
            Text = "Priority: fullscreen → favorites → pinned → regular apps." }, 0, 4);
        var sourceViewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        sourceViewport.Controls.Add(layout);
        _sourcesTab.Controls.Add(sourceViewport);
        _controls.SetDetectionControls(_streamDetection, _catalog, _appleAutoRead, _followBits);
        _mixerTab.Controls.Add(_mixer);
        _controlsTab.Controls.Add(_controls);
        _diagnosticsTab.Controls.Add(_diagnostics);
        _tabs.TabPages.AddRange([_sourcesTab, _mixerTab, _controlsTab, _diagnosticsTab]);
        _tabs.SelectedIndex = 0;
        var content = new Panel { Dock = DockStyle.Fill };
        _sectionTitle.Font = new Font(Font.FontFamily, 20, FontStyle.Bold);
        content.Controls.Add(_tabs); content.Controls.Add(_sectionTitle);
        Controls.Add(content);
        AddNavigation();
        AddWindowHeader();
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            UpdateNavigation();
            if (_tabs.SelectedIndex >= 0 && _tabs.SelectedIndex < _navigationButtons.Count)
                _navigationViewport.ScrollControlIntoView(_navigationButtons[_tabs.SelectedIndex]);
            MixerVisibilityChanged?.Invoke(); DiagnosticsVisibilityChanged?.Invoke();
        };
        VisibleChanged += (_, _) => { MixerVisibilityChanged?.Invoke(); DiagnosticsVisibilityChanged?.Invoke(); };
        _mixer.ChangeRequested += change => VolumeChangeRequested?.Invoke(change);
        _controls.GlobalHotkeysChanged += enabled => GlobalHotkeysChanged?.Invoke(enabled);
        _controls.FavoriteAutoSwitchChanged += enabled => FavoriteAutoSwitchChanged?.Invoke(enabled);
        _controls.FullscreenAutoSwitchChanged += enabled => FullscreenAutoSwitchChanged?.Invoke(enabled);
        _controls.HotkeyRetryRequested += () => HotkeyRetryRequested?.Invoke();

        _devices.SelectedIndexChanged += (_, _) => { if (!_rendering && _devices.SelectedItem is DeviceChoice d && d.Id is not null) DeviceSelected?.Invoke(d.Id); };
        _sources.SelectedIndexChanged += (_, _) =>
        {
            if (_rendering) return;
            _selectedRow = _sources.SelectedItems.Count == 1 ? _sources.SelectedItems[0].Tag as SourceListEntry : null;
            UpdateSourceActions();
        };
        _sources.ItemActivate += (_, _) =>
        {
            if (_rendering || _rowActionsLocked || _selectedRow is not { IsMaster: false } entry) return;
            if (entry.LiveSource is { } source) SourceSelected?.Invoke(source);
            else if (entry.CanSelectSaved) SavedSourceSelected?.Invoke(entry.Identity, entry.Name);
        };
        _rates.SelectedIndexChanged += (_, _) => { if (!_rendering && _rates.SelectedItem is RateChoice r) RateSelected?.Invoke(r.Rate); };
        _depths.SelectedIndexChanged += (_, _) => { if (!_rendering && _depths.SelectedItem is DepthChoice depth) BitDepthSelected?.Invoke(depth.Bits); };
        _enabled.CheckedChanged += (_, _) => { if (!_rendering) SwitchingEnabledChanged?.Invoke(_enabled.Checked); };
        _catalog.CheckedChanged += (_, _) => { if (!_rendering) CatalogChanged?.Invoke(_catalog.Checked); };
        _appleAutoRead.CheckedChanged += (_, _) => { if (!_rendering) AppleAutoReadChanged?.Invoke(_appleAutoRead.Checked); };
        _streamDetection.CheckedChanged += (_, _) => { if (!_rendering) StreamDetectionChanged?.Invoke(_streamDetection.Checked); };
        _followBits.CheckedChanged += (_, _) => { if (!_rendering) FollowBitDepthChanged?.Invoke(_followBits.Checked); };
        _refresh.Click += (_, _) => RetryRequested?.Invoke();
        _release.Click += (_, _) => ReleaseRequested?.Invoke();
        _favorite.Click += (_, _) =>
        {
            if (!_rendering && _favorite.Enabled && _selectedRow is { } row)
                FavoriteChanged?.Invoke(row.Identity, row.Name, !row.IsFavorite);
        };
        _pin.Click += (_, _) =>
        {
            if (!_rendering && _pin.Enabled && _selectedRow is { } row)
                PinChanged?.Invoke(row.Identity, row.Name, !row.IsPinned);
        };
        _tips.SetToolTip(_pin, "◆ Pins persist across restarts. Playing pinned apps rank below favorites and above regular apps when automatic source priority is on.");
        _tips.SetToolTip(_catalog, "Apple Music only; this does not detect rates for Spotify, browsers, or other apps. Catalog metadata describes an available lossless asset, not the actual decoded stream. Off by default.");
        _tips.SetToolTip(_appleAutoRead, "When Apple Music is in front, briefly read and close its Lossless details for the current track. Requires Apple Music track format detection.");
        _tips.SetToolTip(_streamDetection, "Reads current Windows Audio Service stream formats and observes WASAPI stream changes. Requires administrator or ETW permissions. This is submitted PCM; apps may resample media internally. Unsupported Windows schemas remain unknown.");
        _tips.SetToolTip(_rates, "Overrides are saved by executable path. The list shows rates the output reports for the current or manually chosen depth and channel count.");
        _tips.SetToolTip(_depths, "Enable Follow source bit depth in Settings to use experimental Auto or save an exact depth for this master.");
        _tips.SetToolTip(_refresh, "Refresh devices, audio sources and existing stream formats without detaching the live listener. Retry detection if it has stopped.");
        _tips.SetToolTip(_favorite, "Save or remove the highlighted source as a favorite. ★ marks favorites.");
        _favorite.AccessibleDescription = "Toggle favorite for the highlighted source without changing the rate master.";
        _sources.AccessibleName = "Audio sources; star marks favorites";
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        MinimalDarkTheme.Apply(this);
        ResumeLayout(performLayout: true);
        UpdateNavigation();
    }

    private void FitSourceColumns(bool resetFixedWidths = false)
    {
        if (!_sources.IsHandleCreated || _sources.IsDisposed || _sources.Disposing || _sources.Columns.Count < 3) return;
        if (resetFixedWidths)
        {
            _sources.Columns[0].Width = (int)Math.Round(175 * _sources.DeviceDpi / 96d);
            _sources.Columns[1].Width = Math.Max((int)Math.Round(110 * _sources.DeviceDpi / 96d),
                TextRenderer.MeasureText("Idle / paused", _sources.Font).Width + 16);
        }
        var rateWidth = Math.Max(120, _sources.ClientSize.Width - _sources.Columns[0].Width - _sources.Columns[1].Width - 1);
        if (_sources.Columns[2].Width != rateWidth) _sources.Columns[2].Width = rateWidth;
    }

    private void AddNavigation()
    {
        var rail = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 4, AutoSize = true,
            Padding = new Padding(0, 12, 0, 0), Tag = "Navigation" };
        rail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < _tabs.TabCount; index++)
        {
            var selectedIndex = index;
            var button = new SidebarButton { Text = _tabs.TabPages[index].Text, AccessibleName = "Open " + _tabs.TabPages[index].Text,
                AccessibleDescription = "Open this audio control section.", Section = index, Dock = DockStyle.Fill };
            button.Click += (_, _) => _tabs.SelectedIndex = selectedIndex;
            rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); rail.Controls.Add(button, 0, index);
            _navigationButtons.Add(button);
        }
        var version = new Label { Dock = DockStyle.Bottom, Height = 58, Text = "Audio control\nv" + Program.DisplayVersion,
            ForeColor = Color.DimGray, Padding = new Padding(14, 12, 0, 0), Tag = "Navigation", AutoEllipsis = true };
        _navigationRail = rail; _navigationVersion = version;
        _navigationViewport.Controls.Add(rail);
        _navigation.Controls.Add(_navigationViewport); _navigation.Controls.Add(version);
        Controls.Add(_navigation);
        _navigation.SizeChanged += (_, _) => UpdateNavigation();
        foreach (var button in _navigationButtons)
            button.GotFocus += (_, _) => _navigationViewport.ScrollControlIntoView(button);
        ClientSizeChanged += (_, _) => UpdateNavigation();
        UpdateNavigation();
    }

    private void UpdateNavigation()
    {
        var compact = ClientSize.Width * 96d / DeviceDpi < 600;
        _navigation.Width = (int)Math.Round((compact ? 56 : 152) * DeviceDpi / 96d);
        if (_navigationRail is { } rail && _navigationVersion is { } version)
        {
            var preferredRow = (int)Math.Round(48 * DeviceDpi / 96d);
            var minimumRow = (int)Math.Round(32 * DeviceDpi / 96d);
            var hideVersion = _navigation.ClientSize.Height < rail.Padding.Vertical + preferredRow * _navigationButtons.Count + version.Height;
            if (_navigationVersionHidden != hideVersion)
            {
                _navigationVersionHidden = hideVersion;
                version.Visible = !hideVersion;
            }
            var available = _navigation.ClientSize.Height - rail.Padding.Vertical - (hideVersion ? 0 : version.Height);
            var rowHeight = Math.Clamp(available / Math.Max(1, _navigationButtons.Count), minimumRow, preferredRow);
            foreach (RowStyle row in rail.RowStyles)
                if (row.Height != rowHeight) row.Height = rowHeight;
        }
        foreach (var button in _navigationButtons) { button.Selected = button.Section == _tabs.SelectedIndex; button.Compact = compact; }
        _sectionTitle.Text = _tabs.SelectedTab?.Text ?? "Audio control";
    }

    private void AddWindowHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 42, AccessibleName = "Window header" };
        var title = new Label { Dock = DockStyle.Fill, AutoSize = false, Text = Program.ProductName,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(18, 0, 0, 0) };
        var close = new Button { Dock = DockStyle.Right, Width = 42, Text = "×", Tag = "WindowChrome",
            AccessibleName = "Hide to tray", AccessibleDescription = "Hide this window and keep audio control running.",
            TabStop = true };
        close.Click += (_, _) => Hide();
        void Drag(object? sender, MouseEventArgs args)
        {
            if (args.Button != MouseButtons.Left || IsDisposed || Disposing) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (nint)2, nint.Zero); // Native caption drag; no input injection.
        }
        header.MouseDown += Drag;
        title.MouseDown += Drag;
        header.Controls.Add(title);
        header.Controls.Add(close);
        Controls.Add(header);
        Paint += (_, args) =>
        {
            using var border = new Pen(MinimalDarkTheme.Border);
            args.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        };
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    public void ShowAtTray()
    {
        if (IsDisposed || Disposing) return; // A queued tray click may arrive during shutdown.
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        FitToWorkingArea(area);
        Show();
        // Showing on another monitor can change DeviceDpi and the non-client frame.
        FitToWorkingArea(area);
        Activate();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0218 && (message.WParam == 4 || message.WParam == 18 || message.WParam == 7))
        {
            PowerEventReceived?.Invoke((int)message.WParam);
            message.Result = (nint)1;
            return;
        }
        // WM_CLOSE must hide a reusable tray popup. WinForms can otherwise dispose it
        // on repeated native close requests even when a FormClosing handler cancels.
        if (message.Msg == 0x0010 && !IsDisposed && !Disposing)
        {
            Hide();
            return;
        }
        base.WndProc(ref message);
    }

    internal void FitToWorkingArea(Rectangle area)
    {
        var preferredClient = new Size((int)Math.Round(680 * DeviceDpi / 96d), (int)Math.Round(740 * DeviceDpi / 96d));
        Bounds = TrayPopupPlacement.Calculate(area, SizeFromClientSize(preferredClient), (int)Math.Round(12 * DeviceDpi / 96d));
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Hide(); return true; }
        if (keyData is (Keys.Control | Keys.Tab) or (Keys.Control | Keys.Shift | Keys.Tab))
        {
            _tabs.SelectedIndex = (_tabs.SelectedIndex + (keyData.HasFlag(Keys.Shift) ? _tabs.TabCount - 1 : 1)) % _tabs.TabCount;
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    internal void SetPowerSuspended(bool suspended) { _mixer.Enabled = _controls.Enabled = !suspended; }
    public void InvalidateSourceRows() => _rowsSignature = "";
    public void SelectSourcesTab() => _tabs.SelectedTab = _sourcesTab;
    public void SelectMixerTab() => _tabs.SelectedTab = _mixerTab;
    public void SelectControlsTab() => _tabs.SelectedTab = _controlsTab;
    public void SelectDiagnosticsTab() => _tabs.SelectedTab = _diagnosticsTab;
    internal FirstRunTutorial CreateTutorial(Action finished) => new(this, _tabs,
    [
        new(0, _devices, "Choose where sound plays", "Pick your speakers or DAC here. Click this tip or the yellow box to go on."),
        new(0, _sources, "Pick the app to follow", "Double-click the app you want as master. A single click selects it for Pin or Favorite."),
        new(0, _rates, "Leave the rate on Auto", "If you know the rate you want, choose it here instead."),
        new(1, _navigationButtons[1], "Change an app’s volume", "Mixer lets you turn each app up, down, or mute it."),
        new(2, _navigationButtons[2], "Choose who goes first", "Settings can let a favorite or fullscreen app take over."),
        new(3, _navigationButtons[3], "Check the sound rate", "Diagnostics shows the rate from the app and the rate Windows uses.")
    ], finished);
    public void RenderDiagnostics(AudioDiagnosticsSnapshot snapshot) { if (DiagnosticsVisible) _diagnostics.Render(snapshot); }
    public void SetHotkeyStatus(string status) => _controls.SetHotkeyStatus(status);
    public void SetBrowserStatus(string status) => _controls.SetBrowserStatus(status);
    public void SetFavoriteAutoSwitchStatus(string status) => _controls.SetFavoriteAutoSwitchStatus(status);
    public void SetFullscreenStatus(string status) => _controls.SetFullscreenStatus(status);
    public void RenderMixer(AudioSessionVolumeCensus census) => _mixer.Render(census);
    public void RenderMeters(AudioMeterFrame frame) => _mixer.RenderMeters(frame);
    public void StopMeters() => _mixer.StopMeters();
    public void ExpireMeters() => _mixer.ExpireMeters();
    public void SetMixerStatus(string status) => _mixer.SetStatus(status);
    public void CompleteMixerWrite(VolumeMixerChange change, AudioSessionVolumeWriteResult result) => _mixer.CompleteWrite(change, result);

    public void Render(IReadOnlyList<AudioDeviceInfo> devices, IReadOnlyList<PlatformAudioSource> sources,
        IReadOnlyList<AudioFormatCandidate> formats, AudioFormatCandidate? current, SourceSettings settings,
        string status, string evidence, bool applying, RateObservation? observation = null,
        IReadOnlyDictionary<string, RateObservation>? streamObservations = null, BrowserDecoderSnapshot? browser = null,
        IReadOnlyDictionary<string, FavoritePlaybackState>? playback = null,
        RateObservation? appleTrackObservation = null)
    {
        _rendering = true;
        try
        {
            _controls.Render(settings.GlobalHotkeysEnabled, settings.BrowserRateDetectionEnabled, settings.FavoriteAutoSwitchEnabled, settings.FullscreenAutoSwitchEnabled);
            if (!ReferenceEquals(_capabilityFormats, formats) || _capabilityChannels != current?.Channels)
            {
                _capabilityFormats = formats;
                _capabilityChannels = current?.Channels;
                var limits = formats.Where(format => format.Channels == _capabilityChannels && format.BitDepth is 16 or 24 or 32)
                    .GroupBy(format => format.BitDepth).OrderBy(group => group.Key)
                    .Select(group => $"{group.Key}-bit · {FormatRate(group.Max(format => format.SampleRateHz))}");
                var capabilities = string.Join(Environment.NewLine, limits);
                _controls.SetOutputCapabilities(capabilities.Length > 0 ? capabilities : "No output formats observed yet.");
            }
            var deviceSignature = string.Join("|", devices.Select(d => $"{d.Id}:{d.FriendlyName}:{d.IsDefault}")) + settings.TargetEndpointId;
            if (_deviceSignature != deviceSignature)
            {
                _deviceSignature = deviceSignature;
                _devices.Items.Clear();
                _devices.Items.Add(new DeviceChoice(null, "Choose a Windows playback device…"));
                foreach (var d in devices)
                    _devices.Items.Add(new DeviceChoice(d.Id, d.IsDefault ? d.FriendlyName + " (Windows default)" : d.FriendlyName));
                if (settings.TargetEndpointId is { } saved && !devices.Any(d => d.Id == saved))
                    _devices.Items.Add(new DeviceChoice(saved, "Saved output (unavailable)"));
                _deviceSelectionIndex = Math.Max(0, _devices.Items.IndexOf(
                    _devices.Items.Cast<DeviceChoice>().FirstOrDefault(d => d.Id == settings.TargetEndpointId) ?? _devices.Items[0]));
            }
            if (_devices.SelectedIndex != _deviceSelectionIndex) _devices.SelectedIndex = _deviceSelectionIndex;
            _format.Text = current is null ? "Output format unavailable" : $"Shared output: {FormatRate(current.SampleRateHz)} · {current.BitDepth}-bit · {current.Channels} ch";
            _master.Text = "Master: " + (settings.SelectedSourceDisplayName ?? "choose an app below");
            _mixer.SetContext(devices.FirstOrDefault(device => device.Id == settings.TargetEndpointId)?.FriendlyName,
                settings.SelectedSourceDisplayName);
            var selectedSource = sources.FirstOrDefault(s => StringComparer.OrdinalIgnoreCase.Equals(s.Identity, settings.SelectedSourceKey));
            _rowSettings = settings;
            _rowActionsLocked = applying;
            var sourceRows = SourceListPresentation.Build(sources, settings, playback);
            var rowsSignature = string.Join("|", sources.Select(s => $"{s.Identity}:{s.SessionToken}:{s.IsActive}"))
                + settings.SelectedSourceKey + ":" + settings.SelectionRevision + ":" + settings.CatalogRateDetectionEnabled
                + ":" + settings.StreamRateDetectionEnabled + ":" + observation + ":" + appleTrackObservation
                + ":" + settings.BrowserRateDetectionEnabled + ":" + browser?.Version
                + ":" + settings.FollowSourceBitDepth
                + ":" + settings.SelectedSourceDisplayName
                + ":" + settings.LastKnownSourceKey + ":" + settings.LastKnownSampleRateHz
                + string.Join(";", sourceRows.Select(row => $"{row.Identity}:{row.Name}:{row.IsFavorite}:{row.IsPinned}:{row.IsMaster}:{row.MediaPlayback}"))
                + string.Join(";", (streamObservations ?? new Dictionary<string, RateObservation>()).OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}"))
                + string.Join(";", settings.ManualSampleRatesHz.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}"))
                + string.Join(";", settings.ManualBitDepths.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}"));
            if (_rowsSignature != rowsSignature)
            {
                _rowsSignature = rowsSignature;
                var selectedKey = _selectedRow?.Identity;
                _selectedRow = sourceRows.FirstOrDefault(entry => StringComparer.OrdinalIgnoreCase.Equals(entry.Identity, selectedKey))
                    ?? sourceRows.FirstOrDefault(entry => entry.IsMaster);
                _sources.BeginUpdate();
                _sources.Items.Clear();
                foreach (var entry in sourceRows)
                {
                    var source = entry.LiveSource;
                    var stream = streamObservations?.FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item.Key, entry.Identity)).Value;
                    var sourceObservation = source?.SupportsAutomaticRate == true ? appleTrackObservation ?? observation : observation;
                    var rate = source is not null ? SourceRatePresentation.Describe(source, settings, sourceObservation, stream, browser, entry.MediaPlayback)
                        : SourceListPresentation.DescribeSavedRate(entry, settings);
                    var row = new ListViewItem([entry.DisplayName, entry.SessionText, rate.Text])
                    {
                        Tag = entry,
                        ToolTipText = (entry.IsFavorite ? "Favorite\n" : entry.IsPinned ? "Pinned\n" : "")
                            + (source?.ExecutablePath ?? (entry.CanSelectSaved ? entry.Identity : "Executable path unavailable; cannot persist this source"))
                            + (source is null ? "" : "\nPIDs: " + string.Join(", ", source.ProcessIds)) + "\n" + rate.Detail
                            + (string.IsNullOrWhiteSpace(rate.Evidence) ? "" : "\n" + rate.Evidence)
                    };
                    _sources.Items.Add(row);
                    if (ReferenceEquals(entry, _selectedRow)) row.Selected = true;
                }
                _sources.ShowItemToolTips = true;
                _sources.EndUpdate();
            }
            int? savedDepth = settings.SelectedSourceKey is { } masterKey && settings.ManualBitDepths.TryGetValue(masterKey, out var bits)
                ? bits : null;
            var targetDepth = settings.FollowSourceBitDepth ? savedDepth ?? current?.BitDepth : current?.BitDepth;
            var rates = formats.Where(f => current is not null && f.BitDepth == targetDepth && f.Channels == current.Channels)
                .Select(f => f.SampleRateHz).Distinct().Order().ToList();
            int? manual = settings.SelectedSourceKey is not null && settings.ManualSampleRatesHz.TryGetValue(settings.SelectedSourceKey, out var value) ? value : null;
            var autoMode = SourceRatePresentation.AutoModeText(selectedSource, settings);
            var rateSignature = string.Join(",", rates) + ":" + manual + ":" + autoMode;
            if (_rateSignature != rateSignature)
            {
                _rateSignature = rateSignature;
                _rates.Items.Clear();
                _rates.Items.Add(new RateChoice(null, autoMode));
                foreach (var hz in rates) _rates.Items.Add(new RateChoice(hz, FormatRate(hz) + " — manual override"));
                _rates.SelectedItem = _rates.Items.Cast<RateChoice>().FirstOrDefault(r => r.Rate == manual);
            }
            var depths = formats.Where(format => format.Channels == current?.Channels && format.BitDepth is 16 or 24 or 32)
                .Select(format => format.BitDepth).Distinct().Order().ToArray();
            var depthSignature = string.Join(",", depths) + ":" + savedDepth;
            if (_depthSignature != depthSignature)
            {
                _depthSignature = depthSignature;
                _depths.Items.Clear();
                _depths.Items.Add(new DepthChoice(null, "Auto (experimental)"));
                foreach (var depth in depths) _depths.Items.Add(new DepthChoice(depth, $"{depth}-bit"));
                _depths.SelectedItem = _depths.Items.Cast<DepthChoice>().FirstOrDefault(choice => choice.Bits == savedDepth);
            }
            _enabled.Checked = settings.SwitchingEnabled;
            _catalog.Checked = settings.CatalogRateDetectionEnabled;
            _appleAutoRead.Checked = settings.AppleMusicAutoReadQuality;
            _appleAutoRead.Enabled = settings.CatalogRateDetectionEnabled && !applying;
            _streamDetection.Checked = settings.StreamRateDetectionEnabled;
            _followBits.Checked = settings.FollowSourceBitDepth;
            _followBits.Enabled = !applying;
            _devices.Enabled = _sources.Enabled = _rates.Enabled = _catalog.Enabled = _release.Enabled = !applying;
            _enabled.Enabled = _refresh.Enabled = _streamDetection.Enabled = !applying;
            _rates.Enabled &= settings.SelectedSourceKey is not null;
            _depths.Enabled = !applying && settings.FollowSourceBitDepth && settings.SelectedSourceKey is not null;
            _release.Enabled &= settings.SelectedSourceKey is not null;
            UpdateSourceActions();
            _status.Text = status;
            _evidence.Text = manual is { } savedRate && !rates.Contains(savedRate)
                ? $"Saved override {FormatRate(savedRate)} is unavailable on this output. Choose Auto or an available rate."
                : savedDepth is { } unavailableDepth && !depths.Contains(unavailableDepth)
                ? $"Saved {unavailableDepth}-bit depth is unavailable on this output. Choose Auto or an available depth."
                : evidence;
        }
        finally { _rendering = false; }
    }

    private void UpdateSourceActions()
    {
        var row = _selectedRow;
        var favorite = row?.IsFavorite == true;
        var pinned = row?.IsPinned == true;
        var canBookmark = !_rowActionsLocked && row is not null
            && SourceFavorites.CanFavorite(row.Identity, row.Name) && (row.LiveSource?.CanPersist ?? true);
        _favorite.Text = favorite ? "★" : "☆";
        _favorite.AccessibleName = favorite ? "Unfavorite selected source" : "Favorite selected source";
        _pin.Text = pinned ? "Unpin" : "Pin";
        _pin.AccessibleName = pinned ? "Unpin selected source" : "Pin selected source";
        _favorite.Enabled = canBookmark && (favorite || _rowSettings?.FavoriteSources.Count < SourceFavorites.MaxFavorites);
        _pin.Enabled = canBookmark && (pinned || _rowSettings?.PinnedSources.Count < SourceFavorites.MaxFavorites);
    }

    internal static string FormatRate(int rate) => SourceRatePresentation.FormatRate(rate);
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { _tips.Dispose(); _sourceRowSpacing.Dispose(); _sourceRowBitmap.Dispose(); }
    }
    private sealed record DeviceChoice(string? Id, string Name) { public override string ToString() => Name; }
    private sealed record RateChoice(int? Rate, string Name) { public override string ToString() => Name; }
    private sealed record DepthChoice(int? Bits, string Name) { public override string ToString() => Name; }
}

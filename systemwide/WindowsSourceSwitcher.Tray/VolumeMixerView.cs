using System.Diagnostics;
using WindowsSourceSwitcher.Tray.Audio;

namespace WindowsSourceSwitcher.Tray;

internal sealed record VolumeMixerChange(AudioSessionVolumeSnapshot Source, float? Volume, bool? Muted);

/// <summary>Presentation only: Windows volume is changed exclusively by explicit user gestures.</summary>
internal sealed class VolumeMixerView : UserControl
{
    private readonly Label _context = new() { AutoSize = true, MaximumSize = new Size(414, 0) };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(414, 0) };
    private readonly FlowLayoutPanel _list = new() { Dock = DockStyle.Fill, AutoScroll = true,
        FlowDirection = FlowDirection.TopDown, WrapContents = false, TabStop = false };
    private readonly Dictionary<string, MixerRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AudioSourcePeak?> _meterSamples = new(StringComparer.OrdinalIgnoreCase);
    private string? _endpointId;
    private long? _meterGeneration;
    private long _meterSequence = -1, _meterTimestamp;
    private static readonly long MeterMaxAge = Stopwatch.Frequency / 2;

    public event Action<VolumeMixerChange>? ChangeRequested;

    public VolumeMixerView()
    {
        Dock = DockStyle.Fill;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_context, 0, 0);
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(414, 0), Margin = new Padding(0, 8, 0, 8),
            Text = "Adjust each app's volume and mute. Meters show signal level; muted apps may still register. The rate master stays unchanged." }, 0, 1);
        layout.Controls.Add(_list, 0, 2);
        layout.Controls.Add(_status, 0, 3);
        Controls.Add(layout);
        _list.SizeChanged += (_, _) => ResizeRows();
        SetContext(null, null);
        SetStatus("Open this tab to read current Windows app volumes.");
    }

    public void SetContext(string? endpointName, string? masterName) =>
        _context.Text = (endpointName ?? "Choose an output on the Sources tab") + "\nRate master: " + (masterName ?? "none selected");

    public void SetStatus(string text) => _status.Text = text;

    public void Render(AudioSessionVolumeCensus census)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(_endpointId, census.EndpointId))
        {
            StopMeters();
            foreach (var row in _rows.Values) row.Dispose();
            _rows.Clear();
            _list.Controls.Clear();
            _endpointId = census.EndpointId;
        }
        var current = census.Sources.Select(source => source.Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _rows.Keys.Where(key => !current.Contains(key)).ToArray())
        {
            var row = _rows[key];
            _rows.Remove(key);
            _list.Controls.Remove(row);
            row.Dispose();
        }
        _list.SuspendLayout();
        try
        {
            foreach (var source in census.Sources.OrderBy(source => source.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!_rows.TryGetValue(source.Identity, out var row))
                {
                    row = new MixerRow(source);
                    row.ChangeRequested += change => ChangeRequested?.Invoke(change);
                    _rows.Add(source.Identity, row);
                    _list.Controls.Add(row);
                }
                row.Render(source);
            }
            ResizeRows();
        }
        finally { _list.ResumeLayout(); }
        SetStatus(census.Error ?? (census.Sources.Count == 0
            ? "No audio sessions on this output. Start an app, then return here."
            : "Changes apply to this output's current sessions. ShareX is excluded."));
    }

    public void CompleteWrite(VolumeMixerChange change, AudioSessionVolumeWriteResult result)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(_endpointId, change.Source.EndpointId)) return;
        if (_rows.TryGetValue(change.Source.Identity, out var row)) row.CompleteWrite(change, result);
        if (!result.Success) SetStatus(result.Error ?? "Windows could not update this app's volume. Refresh and try again.");
    }

    public void RenderMeters(AudioMeterFrame frame) => RenderMeters(frame, Stopwatch.GetTimestamp());

    internal void RenderMeters(AudioMeterFrame frame, long nowTimestamp)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(_endpointId, frame.EndpointId)) return;
        if (_meterGeneration is { } generation && (frame.Generation < generation
            || frame.Generation == generation && (frame.Sequence <= _meterSequence || frame.SampledAtTimestamp < _meterTimestamp))) return;
        if (_meterGeneration != frame.Generation) _meterTimestamp = 0;
        _meterGeneration = frame.Generation;
        _meterSequence = frame.Sequence;
        var expired = frame.SampledAtTimestamp <= 0 || nowTimestamp - frame.SampledAtTimestamp > MeterMaxAge
            || frame.SampledAtTimestamp - nowTimestamp > Stopwatch.Frequency / 10;
        _meterTimestamp = expired ? 0 : frame.SampledAtTimestamp;
        // Reuse storage on the 20 Hz path. Duplicate identities still fail closed,
        // without constructing grouping objects and a new dictionary every frame.
        _meterSamples.Clear();
        foreach (var sample in frame.Sources)
            if (!_meterSamples.TryAdd(sample.Identity, sample)) _meterSamples[sample.Identity] = null;
        foreach (var pair in _rows)
        {
            _meterSamples.TryGetValue(pair.Key, out var sample);
            pair.Value.RenderMeter(expired ? null : sample, frame.SampledAtTimestamp,
                expired ? "The audio level sample expired." : frame.Error);
        }
    }

    public void ExpireMeters() => ExpireMeters(Stopwatch.GetTimestamp());

    internal void ExpireMeters(long nowTimestamp)
    {
        if (_meterGeneration is not null && nowTimestamp - _meterTimestamp > MeterMaxAge)
            foreach (var row in _rows.Values) row.ClearMeter("The audio level sample expired.");
    }

    public void StopMeters()
    {
        _meterSamples.Clear();
        foreach (var row in _rows.Values) row.ClearMeter("Live metering is paused.");
        _meterGeneration = null;
        _meterSequence = -1;
        _meterTimestamp = 0;
    }

    private void ResizeRows()
    {
        var width = Math.Max(240, _list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 6);
        foreach (var row in _rows.Values) row.Width = width;
    }

    private sealed class MixerRow : SectionCard
    {
        private readonly Label _name = new() { AutoSize = true, AutoEllipsis = true, Dock = DockStyle.Fill };
        private readonly Label _value = new() { AutoSize = false, Width = 56, TextAlign = ContentAlignment.MiddleRight };
        private readonly Label _detail = new() { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, ForeColor = Color.DimGray };
        private readonly TrackBar _slider = new MinimalVolumeSlider() { Minimum = 0, Maximum = 100, TickStyle = TickStyle.None,
            SmallChange = 1, LargeChange = 5, Dock = DockStyle.Fill, AutoSize = false, Height = 32, Margin = Padding.Empty };
        private readonly CheckBox _mute = new MinimalCheckBox() { Text = "Mute", AutoSize = true, Anchor = AnchorStyles.Left };
        private readonly AudioPeakMeterView _meter = new();
        private AudioSessionVolumeSnapshot _source;
        private AudioSessionVolumeSnapshot? _gestureSource;
        private float? _pendingVolume;
        private bool? _pendingMute;
        private bool _updating, _dragging, _gestureInvalidated;
        private string? _writeError;

        public event Action<VolumeMixerChange>? ChangeRequested;

        public MixerRow(AudioSessionVolumeSnapshot source)
        {
            _source = source;
            Height = 132;
            Margin = new Padding(0, 0, 0, 8);
            Padding = new Padding(12);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(_name, 0, 0);
            layout.SetColumnSpan(_name, 3);
            layout.Controls.Add(_slider, 0, 1);
            layout.Controls.Add(_value, 1, 1);
            layout.Controls.Add(_mute, 2, 1);
            layout.Controls.Add(_meter, 0, 2);
            layout.SetColumnSpan(_meter, 3);
            layout.Controls.Add(_detail, 0, 3);
            layout.SetColumnSpan(_detail, 3);
            Controls.Add(layout);
            _slider.MouseDown += (_, _) => { _dragging = true; _gestureInvalidated = false; _gestureSource = _source; };
            _slider.MouseUp += (_, _) =>
            {
                var refreshAfterInvalidatedGesture = _gestureInvalidated;
                if (_dragging) RequestVolume();
                _dragging = false;
                _gestureInvalidated = false;
                _gestureSource = null;
                if (refreshAfterInvalidatedGesture) Render(_source);
            };
            _slider.KeyUp += (_, args) =>
            {
                if (_source.HasMixedVolumes && args.KeyCode is Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.PageDown or Keys.PageUp)
                    RequestVolume();
            };
            _slider.ValueChanged += (_, _) => { if (!_updating) RequestVolume(); };
            _mute.CheckStateChanged += (_, _) =>
            {
                if (_updating || !_source.CanControl) return;
                _pendingMute = _mute.Checked;
                _writeError = null;
                ChangeRequested?.Invoke(new(_source, null, _mute.Checked));
            };
            Render(source);
        }

        private void RequestVolume()
        {
            if (_updating || _gestureInvalidated || !_source.CanControl) return;
            _pendingVolume = _slider.Value / 100f;
            _writeError = null;
            _value.Text = _slider.Value + "%";
            ChangeRequested?.Invoke(new(_gestureSource ?? _source, _pendingVolume, null));
        }

        public void Render(AudioSessionVolumeSnapshot source)
        {
            if (!SameSessions(_source.Sessions, source.Sessions))
            {
                ClearMeter("This app's audio session changed; waiting for a current reading.");
                _pendingVolume = null;
                _pendingMute = null;
                _gestureSource = null;
                _gestureInvalidated = _dragging;
                _writeError = null;
            }
            _source = source;
            _updating = true;
            try
            {
                _name.Text = source.Name;
                _slider.AccessibleName = source.Name + " volume";
                _mute.AccessibleName = source.Name + " mute";
                _meter.AccessibleName = source.Name + " audio level";
                _slider.AccessibleDescription = source.HasMixedVolumes
                    ? "Different sessions have different volumes. Moving this slider sets the app's current sessions to one volume."
                    : "Windows audio-session volume from zero to one hundred percent.";
                if ((!_dragging || _gestureInvalidated) && _pendingVolume is null)
                {
                    if (source.Volume is { } volume)
                    {
                        _slider.Value = Math.Clamp((int)Math.Round(volume * 100), 0, 100);
                        _value.Text = _slider.Value + "%";
                    }
                    else _value.Text = source.HasMixedVolumes ? "Mixed" : "—";
                }
                if (_pendingMute is null)
                    _mute.CheckState = source.HasMixedMute ? CheckState.Indeterminate : source.Muted == true ? CheckState.Checked : CheckState.Unchecked;
                _slider.Enabled = _mute.Enabled = source.CanControl;
                _detail.Text = source.Error is { } sourceError && !source.CanControl ? "Read only · " + sourceError
                    : source.Error ?? _writeError ?? string.Join(" · ", new[] {
                    source.IsActive ? "Active session" : "Idle session",
                    source.Sessions.Count == 1 ? "1 session" : source.Sessions.Count + " sessions",
                    source.HasMixedVolumes || source.HasMixedMute ? "Mixed settings" : null }.Where(text => text is not null));
            }
            finally { _updating = false; }
        }

        public void RenderMeter(AudioSourcePeak? sample, long timestamp, string? error)
        {
            if (sample is null || error is not null || !SameSessions(_source.Sessions, sample.Sessions))
            {
                ClearMeter(error ?? "No current reading matches this app's audio sessions.");
                return;
            }
            _meter.SetPeak(sample.Peak, TimeSpan.FromSeconds((double)timestamp / Stopwatch.Frequency), sample.Error);
        }

        public void ClearMeter(string reason) => _meter.SetUnavailable(reason);

        private static bool SameSessions(IReadOnlyList<VolumeSessionIdentity> left, IReadOnlyList<VolumeSessionIdentity> right)
        {
            if (left.Count == 0 || left.Count != right.Count) return false;
            if (left.Count == 1) return AudioSessionVolumeController.Matches(left[0], right[0]);
            // Validate both directions, including duplicates, without per-frame LINQ closures.
            for (var pass = 0; pass < 2; pass++)
            {
                var source = pass == 0 ? left : right;
                var target = pass == 0 ? right : left;
                for (var index = 0; index < source.Count; index++)
                {
                    var matches = 0;
                    for (var other = 0; other < target.Count; other++)
                        if (AudioSessionVolumeController.Matches(source[index], target[other]) && ++matches > 1) return false;
                    if (matches != 1) return false;
                }
            }
            return true;
        }

        public void CompleteWrite(VolumeMixerChange change, AudioSessionVolumeWriteResult result)
        {
            if (!SameSessions(_source.Sessions, change.Source.Sessions)) return;
            _writeError = result.Success ? null : result.Error ?? "Windows could not update this session. Refresh to retry.";
            if (change.Volume is { } volume && _pendingVolume == volume) _pendingVolume = null;
            if (change.Muted is { } mute && _pendingMute == mute) _pendingMute = null;
            if (result.Confirmed is { } confirmed && SameSessions(_source.Sessions, confirmed.Sessions)) Render(confirmed);
            else if (!result.Success)
            {
                Render(_source);
                _detail.Text = _writeError;
            }
        }
    }
}

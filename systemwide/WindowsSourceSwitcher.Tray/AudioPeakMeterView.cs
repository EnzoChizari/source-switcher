using System.Globalization;

namespace WindowsSourceSwitcher.Tray;

/// <summary>A logarithmic presentation envelope. It never changes or processes audio.</summary>
internal sealed class AudioMeterEnvelope
{
    internal const double FloorDb = -60;
    private static readonly TimeSpan Release = TimeSpan.FromMilliseconds(200);
    private TimeSpan? _lastTimestamp;

    public bool IsAvailable { get; private set; }
    public double Decibels { get; private set; } = FloorDb;
    public double DisplayFraction { get; private set; }
    public bool IsQuiet => IsAvailable && Decibels <= FloorDb;

    public void Update(float? peak, TimeSpan timestamp)
    {
        if (peak is not { } value || !float.IsFinite(value) || value is < 0 or > 1)
        {
            Clear();
            return;
        }
        if (_lastTimestamp is { } last && timestamp < last) return;
        Decibels = value > .001f ? Math.Max(FloorDb, 20 * Math.Log10(value)) : FloorDb;
        var target = (Decibels - FloorDb) / -FloorDb;
        var elapsed = _lastTimestamp is { } previous ? timestamp - previous : Release;
        DisplayFraction = !IsAvailable || target >= DisplayFraction ? target
            : Math.Max(target, DisplayFraction - elapsed.TotalMilliseconds / Release.TotalMilliseconds);
        IsAvailable = true;
        _lastTimestamp = timestamp;
    }

    public void Clear()
    {
        IsAvailable = false;
        Decibels = FloorDb;
        DisplayFraction = 0;
        _lastTimestamp = null;
    }
}

/// <summary>Painted meter with a queryable accessible value; no per-frame accessibility notifications.</summary>
internal sealed class AudioPeakMeterView : Control
{
    private readonly AudioMeterEnvelope _envelope = new();
    private string _levelText = "Unavailable";
    private readonly SolidBrush _trackBrush = new(Color.Black);
    private readonly SolidBrush _fillBrush = new(Color.Black);
    private string? _unavailableReason;
    private const string SignalDescription = "Windows-reported audio-session peak. This is not a physical DAC or post-mute loudness measurement.";

    public bool IsAvailable => _envelope.IsAvailable;
    public double DisplayFraction => _envelope.DisplayFraction;
    public string LevelText => _levelText;

    public AudioPeakMeterView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Dock = DockStyle.Fill;
        TabStop = false;
        Height = 22;
        MinimumSize = new Size(140, 20);
        AccessibleRole = AccessibleRole.ProgressBar;
        AccessibleDescription = SignalDescription;
    }

    public void SetPeak(float? peak, TimeSpan timestamp, string? error = null)
    {
        var oldLevel = LevelText;
        var oldFraction = DisplayFraction;
        _unavailableReason = error;
        _envelope.Update(error is null ? peak : null, timestamp);
        _levelText = !_envelope.IsAvailable ? "Unavailable" : _envelope.IsQuiet ? "Quiet"
            : _envelope.Decibels.ToString("0.0", CultureInfo.InvariantCulture) + " dBFS";
        if (oldLevel != LevelText || oldFraction != DisplayFraction) Invalidate();
    }

    public void SetUnavailable(string? reason)
    {
        _unavailableReason = reason;
        if (!IsAvailable) return;
        _envelope.Clear();
        _levelText = "Unavailable";
        Invalidate();
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new MeterAccessibleObject(this);

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        var textWidth = 92;
        var bar = new Rectangle(0, Math.Max(1, (Height - 8) / 2), Math.Max(1, Width - textWidth - 8), 8);
        if (_trackBrush.Color != MinimalDarkTheme.RaisedSurface) _trackBrush.Color = MinimalDarkTheme.RaisedSurface;
        args.Graphics.FillRectangle(_trackBrush, bar);
        if (IsAvailable && DisplayFraction > 0)
        {
            var color = DisplayFraction > .95 ? Color.Firebrick : DisplayFraction > .8 ? Color.Goldenrod : MinimalDarkTheme.Accent;
            if (_fillBrush.Color != color) _fillBrush.Color = color;
            args.Graphics.FillRectangle(_fillBrush, new Rectangle(bar.X, bar.Y, Math.Max(1, (int)Math.Round(bar.Width * DisplayFraction)), bar.Height));
        }
        TextRenderer.DrawText(args.Graphics, LevelText, Font,
            new Rectangle(Width - textWidth, 0, textWidth, Height), IsAvailable ? ForeColor : MinimalDarkTheme.MutedText,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { _trackBrush.Dispose(); _fillBrush.Dispose(); }
    }

    private sealed class MeterAccessibleObject(AudioPeakMeterView owner) : ControlAccessibleObject(owner)
    {
        public override string? Value { get => owner.LevelText; set { } }
        public override AccessibleStates State => base.State | AccessibleStates.ReadOnly;
        public override string? Description => owner.IsAvailable ? SignalDescription
            : SignalDescription + " " + (owner._unavailableReason ?? "No current meter reading is available.");
    }
}

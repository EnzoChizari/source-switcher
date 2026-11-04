using System.Drawing.Drawing2D;

namespace WindowsSourceSwitcher.Tray;

/// <summary>A painted checkbox retaining CheckBox click, mnemonic, keyboard and UIA behavior.</summary>
internal sealed class MinimalCheckBox : CheckBox
{
    private bool _hovered;

    public MinimalCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = true;
    }

    private int Scale(int pixels) => Math.Max(1, (int)Math.Round(pixels * DeviceDpi / 96d));

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine);
        return new Size(text.Width + Scale(25) + Padding.Horizontal,
            Math.Max(Scale(20), text.Height) + Padding.Vertical);
    }

    protected override void OnCheckedChanged(EventArgs args) { base.OnCheckedChanged(args); Invalidate(); }
    protected override void OnCheckStateChanged(EventArgs args) { base.OnCheckStateChanged(args); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs args) { base.OnEnabledChanged(args); Invalidate(); }
    protected override void OnGotFocus(EventArgs args) { base.OnGotFocus(args); Invalidate(); }
    protected override void OnLostFocus(EventArgs args) { base.OnLostFocus(args); Invalidate(); }
    protected override void OnMouseEnter(EventArgs args) { base.OnMouseEnter(args); _hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs args) { base.OnMouseLeave(args); _hovered = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs args)
    {
        if (SystemInformation.HighContrast) { base.OnPaint(args); return; }
        args.Graphics.Clear(BackColor);
        var box = new Rectangle(Padding.Left + Scale(1), Math.Max(0, (Height - Scale(15)) / 2), Scale(15), Scale(15));
        using var fill = new SolidBrush(Checked ? (Enabled ? MinimalDarkTheme.Accent : MinimalDarkTheme.RaisedSurface)
            : _hovered && Enabled ? MinimalDarkTheme.HoverSurface : MinimalDarkTheme.Surface);
        args.Graphics.FillRectangle(fill, box);
        using var border = new Pen(Enabled ? MinimalDarkTheme.MutedText : MinimalDarkTheme.Border);
        args.Graphics.DrawRectangle(border, box.X, box.Y, box.Width - 1, box.Height - 1);
        if (CheckState != CheckState.Unchecked)
        {
            args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var check = new Pen(Enabled ? MinimalDarkTheme.Background : MinimalDarkTheme.MutedText, Scale(2));
            if (CheckState == CheckState.Indeterminate)
                args.Graphics.DrawLine(check, box.Left + Scale(3), box.Top + Scale(7), box.Right - Scale(3), box.Top + Scale(7));
            else
                args.Graphics.DrawLines(check, new Point[] { new(box.Left + Scale(3), box.Top + Scale(7)),
                    new(box.Left + Scale(6), box.Top + Scale(10)), new(box.Left + Scale(12), box.Top + Scale(4)) });
            args.Graphics.SmoothingMode = SmoothingMode.None;
        }
        var textBounds = new Rectangle(box.Right + Scale(8), Padding.Top,
            Math.Max(0, Width - box.Right - Scale(8) - Padding.Right), Height - Padding.Vertical);
        TextRenderer.DrawText(args.Graphics, Text, Font, textBounds, Enabled ? ForeColor : MinimalDarkTheme.MutedText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                | (UseMnemonic ? ShowKeyboardCues ? 0 : TextFormatFlags.HidePrefix : TextFormatFlags.NoPrefix));
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(args.Graphics, Rectangle.Inflate(ClientRectangle, -1, -1), MinimalDarkTheme.Text, BackColor);
    }
}

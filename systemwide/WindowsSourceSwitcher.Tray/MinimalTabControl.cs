namespace WindowsSourceSwitcher.Tray;

/// <summary>
/// A native TabControl with a quiet painted surface. Native selection, keyboard navigation,
/// TabPage children, accessibility and automation identifiers stay with the base control.
/// </summary>
internal sealed class MinimalTabControl : TabControl
{
    private bool _navigationOnly;
    public bool NavigationOnly { get => _navigationOnly; set { _navigationOnly = value; UpdateTabSize(); PerformLayout(); Invalidate(); } }
    public override Rectangle DisplayRectangle => NavigationOnly ? ClientRectangle : base.DisplayRectangle;
    public MinimalTabControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SizeMode = TabSizeMode.Fixed;
        Padding = new Point(14, 8);
        UpdateTabSize();
    }

    private void UpdateTabSize() => ItemSize = NavigationOnly ? new Size(1, 1) : new Size(Scale(108), Scale(38));
    private int Scale(int pixels) => Math.Max(1, (int)Math.Round(pixels * DeviceDpi / 96d));

    protected override void OnDpiChangedAfterParent(EventArgs args)
    {
        base.OnDpiChangedAfterParent(args);
        UpdateTabSize();
    }

    protected override void OnSelectedIndexChanged(EventArgs args)
    {
        base.OnSelectedIndexChanged(args);
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs args) { base.OnGotFocus(args); Invalidate(); }
    protected override void OnLostFocus(EventArgs args) { base.OnLostFocus(args); Invalidate(); }

    protected override void OnPaint(PaintEventArgs args)
    {
        args.Graphics.Clear(MinimalDarkTheme.Background);
        if (NavigationOnly) return;
        for (var index = 0; index < TabPages.Count; index++)
        {
            var bounds = GetTabRect(index);
            var selected = index == SelectedIndex;
            if (selected)
            {
                using var surface = new SolidBrush(MinimalDarkTheme.RaisedSurface);
                args.Graphics.FillRectangle(surface, Rectangle.Inflate(bounds, -3, -4));
                using var accent = new SolidBrush(MinimalDarkTheme.Accent);
                args.Graphics.FillRectangle(accent, bounds.X + Scale(16), bounds.Bottom - Scale(3),
                    Math.Max(1, bounds.Width - Scale(32)), Scale(2));
            }
            TextRenderer.DrawText(args.Graphics, TabPages[index].Text, Font, bounds,
                selected ? MinimalDarkTheme.Text : MinimalDarkTheme.MutedText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.EndEllipsis | (ShowKeyboardCues ? 0 : TextFormatFlags.HidePrefix));
            if (selected && Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(args.Graphics, Rectangle.Inflate(bounds, -5, -5),
                    MinimalDarkTheme.Text, MinimalDarkTheme.Background);
        }
        // Do not invoke native tab painting after the flat surface has been drawn.
        // Child TabPages are independent native controls and paint normally.
    }
}

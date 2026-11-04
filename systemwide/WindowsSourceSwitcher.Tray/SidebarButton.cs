namespace WindowsSourceSwitcher.Tray;

internal sealed class SidebarButton : Button
{
    private bool _selected, _compact;
    public int Section { get; init; }
    public bool Selected { get => _selected; set { if (_selected == value) return; _selected = value; Invalidate(); } }
    public bool Compact { get => _compact; set { if (_compact == value) return; _compact = value; Invalidate(); } }
    public SidebarButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Height = 44;
        Margin = new Padding(6, 2, 6, 2);
        AccessibleRole = AccessibleRole.PushButton;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnPaint(PaintEventArgs args)
    {
        var background = Selected ? MinimalDarkTheme.RaisedSurface : MinimalDarkTheme.NavigationSurface;
        if (ClientRectangle.Contains(PointToClient(Cursor.Position))) background = MinimalDarkTheme.HoverSurface;
        args.Graphics.Clear(background);
        var scale = DeviceDpi / 96f;
        int Px(int value) => (int)Math.Round(value * scale);
        var foreground = Selected ? MinimalDarkTheme.Text : MinimalDarkTheme.MutedText;
        using var pen = new Pen(foreground, Math.Max(1, scale));
        var x = Px(17); var y = Height / 2 - Px(7); var unit = Px(6);
        if (Section == 0)
        {
            for (var row = 0; row < 2; row++) for (var column = 0; column < 2; column++)
                args.Graphics.DrawRectangle(pen, x + column * (unit + Px(2)), y + row * (unit + Px(2)), unit, unit);
        }
        else if (Section == 1)
        {
            for (var column = 0; column < 3; column++)
            {
                var left = x + column * Px(6);
                args.Graphics.DrawLine(pen, left, y, left, y + Px(15));
                args.Graphics.DrawRectangle(pen, left - Px(2), y + Px(3 + column * 3), Px(4), Px(3));
            }
        }
        else if (Section == 2)
        {
            args.Graphics.DrawEllipse(pen, x, y, Px(14), Px(14));
            args.Graphics.DrawEllipse(pen, x + Px(4), y + Px(4), Px(6), Px(6));
        }
        else
        {
            for (var row = 0; row < 3; row++) args.Graphics.DrawLine(pen, x, y + Px(3 + row * 5), x + Px(15), y + Px(3 + row * 5));
        }
        if (!Compact) TextRenderer.DrawText(args.Graphics, Text, Font,
            new Rectangle(Px(44), 0, Math.Max(1, Width - Px(48)), Height), foreground,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (Selected)
        {
            using var accent = new SolidBrush(MinimalDarkTheme.Accent);
            args.Graphics.FillRectangle(accent, Px(2), Px(12), Px(3), Height - Px(24));
        }
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(args.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
    }
}

using System.Drawing.Drawing2D;

namespace WindowsSourceSwitcher.Tray;

internal class SectionCard : Panel
{
    public Color? FillColor { get; set; }

    public SectionCard()
    {
        Tag = "Card";
        Padding = new Padding(14);
        Margin = new Padding(0, 0, 0, 10);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaintBackground(PaintEventArgs args)
    {
        args.Graphics.Clear(Parent?.BackColor ?? MinimalDarkTheme.Background);
        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        var radius = Math.Min((int)Math.Round(10 * DeviceDpi / 96d), Math.Min(bounds.Width, bounds.Height) / 2);
        using var path = new GraphicsPath();
        if (radius < 1) path.AddRectangle(bounds);
        else
        {
            var diameter = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
        }
        using var surface = new SolidBrush(FillColor ?? MinimalDarkTheme.Surface);
        args.Graphics.FillPath(surface, path);
    }
}

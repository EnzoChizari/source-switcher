using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WindowsSourceSwitcher.Tray;

/// <summary>Custom drawing over the native trackbar: native input, range and UIA remain intact.</summary>
internal sealed class MinimalVolumeSlider : TrackBar
{
    private readonly Pen _track = new(Color.Gray, 2);
    private readonly Pen _fill = new(Color.Black, 2);
    private readonly SolidBrush _thumb = new(Color.Black);
    protected override CreateParams CreateParams
    {
        get { var parameters = base.CreateParams; if (!SystemInformation.HighContrast) parameters.Style |= 0x40; return parameters; } // TBS_FIXEDLENGTH
    }
    public MinimalVolumeSlider() { TickStyle = TickStyle.None; AutoSize = false; Height = 32; }
    protected override void OnHandleCreated(EventArgs args)
    {
        base.OnHandleCreated(args);
        SetThumbLength();
    }
    protected override void OnDpiChangedAfterParent(EventArgs args)
    {
        base.OnDpiChangedAfterParent(args);
        SetThumbLength();
    }
    private void SetThumbLength()
    {
        if (IsHandleCreated && !SystemInformation.HighContrast) SendMessage(Handle, 0x041B /* TBM_SETTHUMBLENGTH */, (nint)Math.Round(36 * DeviceDpi / 96d), 0);
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x204E /* reflected WM_NOTIFY */ && message.LParam != 0
            && !SystemInformation.HighContrast && Orientation == Orientation.Horizontal)
        {
            var draw = Marshal.PtrToStructure<CustomDraw>(message.LParam);
            if (draw.Header.Code == -12 /* NM_CUSTOMDRAW */)
            {
                if (draw.Stage == 1) { message.Result = (nint)0x20; return; } // Notify item drawing.
                if (draw.Stage == 0x10001 && draw.Item is 2 or 3 && draw.DeviceContext != 0)
                {
                    using var graphics = Graphics.FromHdc(draw.DeviceContext);
                    PaintItem(graphics, (int)draw.Item, draw.Bounds);
                    message.Result = (nint)4; return; // CDRF_SKIPDEFAULT for this item.
                }
            }
        }
        base.WndProc(ref message);
    }
    private void PaintItem(Graphics graphics, int item, NativeRectangle bounds)
    {
        var thumb = new NativeRectangle();
        SendRectangle(Handle, 0x0419 /* TBM_GETTHUMBRECT */, 0, ref thumb);
        var scale = DeviceDpi / 96f;
        var centerX = (thumb.Left + thumb.Right) / 2f;
        var centerY = (thumb.Top + thumb.Bottom) / 2f;
        var accent = Enabled ? Color.FromArgb(255, 152, 0) : MinimalDarkTheme.MutedText;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (item == 3)
        {
            _track.Color = MinimalDarkTheme.Border; _track.Width = Math.Max(1, 2 * scale);
            _fill.Color = accent; _fill.Width = _track.Width;
            _track.StartCap = _track.EndCap = _fill.StartCap = _fill.EndCap = LineCap.Round;
            graphics.DrawLine(_track, bounds.Left, centerY, bounds.Right, centerY);
            if (RightToLeft == RightToLeft.Yes) graphics.DrawLine(_fill, centerX, centerY, bounds.Right, centerY);
            else graphics.DrawLine(_fill, bounds.Left, centerY, centerX, centerY);
        }
        else
        {
            var diameter = Math.Min(20 * scale, Math.Min(thumb.Right - thumb.Left, thumb.Bottom - thumb.Top));
            _thumb.Color = accent;
            graphics.FillEllipse(_thumb, centerX - diameter / 2, centerY - diameter / 2, diameter, diameter);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(graphics, Rectangle.Round(new RectangleF(centerX - diameter / 2 - 3, centerY - diameter / 2 - 3, diameter + 6, diameter + 6)));
        }
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { _track.Dispose(); _fill.Dispose(); _thumb.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Header { public nint Window; public nuint Id; public int Code; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRectangle { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct CustomDraw
    {
        public Header Header; public uint Stage; public nint DeviceContext; public NativeRectangle Bounds;
        public nuint Item; public uint State; public nint Parameter;
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendRectangle(nint window, uint message, nint wParam, ref NativeRectangle rectangle);
}

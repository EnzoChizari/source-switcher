using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WindowsSourceSwitcher.Tray;

/// <summary>
/// Paints only this process's existing native nonclient scrollbars. Windows retains their
/// geometry, range, hit testing, drag/wheel/key handling and accessibility. No timer or hook
/// outside the attached control is installed; high contrast uses the unmodified native paint.
/// </summary>
internal sealed class DarkScrollBarPainter : NativeWindow, IDisposable
{
    private const uint Invisible = 0x8000, Offscreen = 0x10000, Pressed = 0x8, HotTracked = 0x80, Unavailable = 0x1;
    private bool _disposed, _painting;

    public DarkScrollBarPainter(IntPtr window)
    {
        if (window == IntPtr.Zero) throw new ArgumentException("An owned control handle is required.", nameof(window));
        GetWindowThreadProcessId(window, out var process);
        if (process != Environment.ProcessId) throw new ArgumentException("Only this app's native controls may be styled.", nameof(window));
        AssignHandle(window);
    }

    public bool IsAttachedTo(IntPtr window) => !_disposed && Handle != IntPtr.Zero && Handle == window;

    protected override void WndProc(ref Message message)
    {
        var originalHandle = Handle;
        base.WndProc(ref message);
        if (message.Msg == 0x0082 /* WM_NCDESTROY */)
        {
            _disposed = true;
            if (Handle != IntPtr.Zero) ReleaseHandle();
            return;
        }
        if (_disposed || _painting || originalHandle == IntPtr.Zero || Handle != originalHandle || SystemInformation.HighContrast) return;
        if (message.Msg == 0x0317 /* WM_PRINT */ && message.WParam != IntPtr.Zero
            && (message.LParam.ToInt64() & 0x2 /* PRF_NONCLIENT */) != 0)
            Paint(message.WParam);
        else if (message.Msg is 0x0085 or 0x0086 or 0x0088 or 0x000F or 0x0005 or 0x0047 or 0x0018 or 0x0007 or 0x0008 or 0x000A
            or 0x0114 or 0x0115 or 0x020A or 0x020E or 0x00A0 or 0x00A1 or 0x00A2
            or 0x00A3 or 0x00A4 or 0x00A5 or 0x0200 or 0x0201 or 0x0202 or 0x02A2
            or 0x02A3 or 0x0113 or 0x007D or 0x001F or 0x00F4)
            Paint(IntPtr.Zero);
        // Also repaint when focus or modal enablement changes, which can redraw the
        // native bars without scrolling. Every message retains its native behavior.
    }

    public void Redraw()
    {
        if (!_disposed && Handle != IntPtr.Zero && !SystemInformation.HighContrast) Paint(IntPtr.Zero);
    }

    private void Paint(IntPtr suppliedDc)
    {
        if (_painting || Handle == IntPtr.Zero || !GetWindowRect(Handle, out var window)) return;
        var vertical = ReadBar(Handle, -5 /* OBJID_VSCROLL */);
        var horizontal = ReadBar(Handle, -6 /* OBJID_HSCROLL */);
        if (vertical is null && horizontal is null) return;
        _painting = true;
        var dc = suppliedDc != IntPtr.Zero ? suppliedDc : GetWindowDC(Handle);
        try
        {
            if (dc == IntPtr.Zero) return;
            using var graphics = Graphics.FromHdc(dc);
            var bounds = new Rectangle(0, 0, window.Right - window.Left, window.Bottom - window.Top);
            Rectangle? verticalBounds = null, horizontalBounds = null;
            if (vertical is { } verticalInfo) verticalBounds = PaintBar(graphics, verticalInfo, window, bounds, true);
            if (horizontal is { } horizontalInfo) horizontalBounds = PaintBar(graphics, horizontalInfo, window, bounds, false);
            if (verticalBounds is { } v && horizontalBounds is { } h)
            {
                // The native two-bar corner follows their measured axes, including a left bar.
                var corner = Rectangle.Intersect(bounds, new Rectangle(v.Left, h.Top, v.Width, h.Height));
                if (!corner.IsEmpty)
                {
                    using var fill = new SolidBrush(MinimalDarkTheme.Background);
                    graphics.FillRectangle(fill, corner);
                }
            }
        }
        finally
        {
            if (suppliedDc == IntPtr.Zero && dc != IntPtr.Zero) ReleaseDC(Handle, dc);
            _painting = false;
        }
    }

    private static NativeScrollBarInfo? ReadBar(IntPtr window, int objectId)
    {
        var info = new NativeScrollBarInfo { Size = Marshal.SizeOf<NativeScrollBarInfo>() };
        return GetScrollBarInfo(window, objectId, ref info) && (info.State & (Invisible | Offscreen)) == 0
            && info.Bounds.Right > info.Bounds.Left && info.Bounds.Bottom > info.Bounds.Top ? info : null;
    }

    private static Rectangle? PaintBar(Graphics graphics, NativeScrollBarInfo info, NativeRectangle window, Rectangle windowBounds, bool vertical)
    {
        var original = Rectangle.FromLTRB(info.Bounds.Left - window.Left, info.Bounds.Top - window.Top,
            info.Bounds.Right - window.Left, info.Bounds.Bottom - window.Top);
        var bar = Rectangle.Intersect(windowBounds, original);
        if (bar.IsEmpty) return null;
        using var track = new SolidBrush(MinimalDarkTheme.Background);
        graphics.FillRectangle(track, bar);
        var length = vertical ? original.Height : original.Width;
        var buttonLength = Math.Clamp(info.LineButton, 0, length / 2);
        var first = vertical ? new Rectangle(original.Left, original.Top, original.Width, buttonLength)
            : new Rectangle(original.Left, original.Top, buttonLength, original.Height);
        var last = vertical ? new Rectangle(original.Left, original.Bottom - buttonLength, original.Width, buttonLength)
            : new Rectangle(original.Right - buttonLength, original.Top, buttonLength, original.Height);
        PaintArrow(graphics, first, info.FirstArrowState, vertical, false);
        PaintArrow(graphics, last, info.LastArrowState, vertical, true);
        var top = Math.Clamp(info.ThumbTop, buttonLength, Math.Max(buttonLength, length - buttonLength));
        var bottom = Math.Clamp(info.ThumbBottom, top, Math.Max(top, length - buttonLength));
        if (bottom > top && (info.ThumbState & (Invisible | Offscreen)) == 0)
        {
            var thumb = vertical ? new Rectangle(original.Left, original.Top + top, original.Width, bottom - top)
                : new Rectangle(original.Left + top, original.Top, bottom - top, original.Height);
            var inset = Math.Max(1, (vertical ? original.Width : original.Height) / 4);
            thumb.Inflate(vertical ? -inset : 0, vertical ? 0 : -inset);
            thumb = Rectangle.Intersect(bar, thumb);
            if (thumb.Width > 0 && thumb.Height > 0)
            {
                using var fill = new SolidBrush((info.ThumbState & (Pressed | HotTracked)) != 0 ? MinimalDarkTheme.MutedText
                    : (info.State & Unavailable) != 0 ? MinimalDarkTheme.Border : Color.FromArgb(107, 107, 119));
                graphics.FillRectangle(fill, thumb);
            }
        }
        return bar;
    }

    private static void PaintArrow(Graphics graphics, Rectangle bounds, uint state, bool vertical, bool forward)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || (state & (Invisible | Offscreen)) != 0) return;
        if ((state & (Pressed | HotTracked)) != 0)
        {
            using var hover = new SolidBrush(MinimalDarkTheme.RaisedSurface);
            graphics.FillRectangle(hover, bounds);
        }
        var center = new PointF(bounds.Left + bounds.Width / 2f, bounds.Top + bounds.Height / 2f);
        var half = Math.Max(2f, Math.Min(bounds.Width, bounds.Height) * .2f);
        var direction = forward ? 1f : -1f;
        PointF[] points = vertical
            ? [new(center.X - half, center.Y - direction * half / 2), new(center.X, center.Y + direction * half / 2), new(center.X + half, center.Y - direction * half / 2)]
            : [new(center.X - direction * half / 2, center.Y - half), new(center.X + direction * half / 2, center.Y), new(center.X - direction * half / 2, center.Y + half)];
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen((state & Unavailable) != 0 ? MinimalDarkTheme.Border : MinimalDarkTheme.MutedText,
            Math.Max(1f, Math.Min(bounds.Width, bounds.Height) / 12f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawLines(pen, points);
        graphics.SmoothingMode = SmoothingMode.None;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Handle != IntPtr.Zero) ReleaseHandle();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeScrollBarInfo
    {
        public int Size;
        public NativeRectangle Bounds;
        public int LineButton, ThumbTop, ThumbBottom, Reserved;
        public uint State, FirstArrowState, BeforeThumbState, ThumbState, AfterThumbState, LastArrowState;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRectangle bounds);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetScrollBarInfo(IntPtr window, int objectId, ref NativeScrollBarInfo info);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
}

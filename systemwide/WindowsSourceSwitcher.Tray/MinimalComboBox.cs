using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WindowsSourceSwitcher.Tray;

/// <summary>
/// Retains the native ComboBox window, drop-down list, input handling and accessibility.
/// Only the collapsed arrow and outer edge are overpainted after native rendering.
/// </summary>
internal sealed class MinimalComboBox : ComboBox
{
    private bool _hovered;
    private DarkScrollBarPainter? _popupScrollBars;

    public MinimalComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        MinimalDarkTheme.Apply(this);
    }

    protected override void OnMouseEnter(EventArgs args) { base.OnMouseEnter(args); _hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs args) { base.OnMouseLeave(args); _hovered = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs args) { base.OnGotFocus(args); Invalidate(); }
    protected override void OnLostFocus(EventArgs args) { base.OnLostFocus(args); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs args) { base.OnEnabledChanged(args); Invalidate(); }
    protected override void OnDropDown(EventArgs args)
    {
        base.OnDropDown(args);
        var info = new ComboBoxInfo { Size = Marshal.SizeOf<ComboBoxInfo>() };
        if (IsHandleCreated && GetComboBoxInfo(Handle, ref info) && info.ListWindow != IntPtr.Zero)
        {
            if (_popupScrollBars?.IsAttachedTo(info.ListWindow) != true)
            {
                _popupScrollBars?.Dispose();
                _popupScrollBars = new DarkScrollBarPainter(info.ListWindow);
            }
            _popupScrollBars.Redraw();
        }
        Invalidate();
    }
    protected override void OnDropDownClosed(EventArgs args) { base.OnDropDownClosed(args); Invalidate(); }

    protected override void OnHandleDestroyed(EventArgs args)
    {
        _popupScrollBars?.Dispose();
        _popupScrollBars = null;
        base.OnHandleDestroyed(args);
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (!IsHandleCreated || IsDisposed || SystemInformation.HighContrast
            || DropDownStyle != ComboBoxStyle.DropDownList || ClientSize.Width <= 1 || ClientSize.Height <= 1) return;
        if (message.Msg == 0x000F /* WM_PAINT */)
        {
            using var graphics = Graphics.FromHwnd(Handle);
            PaintChrome(graphics);
        }
        else if (message.Msg is 0x0317 or 0x0318 /* WM_PRINT / WM_PRINTCLIENT */ && message.WParam != IntPtr.Zero)
        {
            // DrawToBitmap/printing supplies its own HDC. Never substitute the screen DC.
            using var graphics = Graphics.FromHdc(message.WParam);
            PaintChrome(graphics);
        }
    }

    private int Scale(int pixels) => Math.Max(1, (int)Math.Round(pixels * DeviceDpi / 96d));

    private void PaintChrome(Graphics graphics)
    {
        var info = new ComboBoxInfo { Size = Marshal.SizeOf<ComboBoxInfo>() };
        if (!GetComboBoxInfo(Handle, ref info) || (info.ButtonState & 0x8000 /* STATE_SYSTEM_INVISIBLE */) != 0) return;
        var arrow = Rectangle.Intersect(ClientRectangle, Rectangle.FromLTRB(info.Button.Left, info.Button.Top, info.Button.Right, info.Button.Bottom));
        if (arrow.Width <= 0 || arrow.Height <= 0) return;
        // Use the native button's measured bounds: hit testing and RTL placement stay native.
        var fillBounds = Rectangle.Intersect(ClientRectangle, Rectangle.Inflate(arrow, 1, 1));
        using var fill = new SolidBrush(Enabled && (_hovered || DroppedDown) ? MinimalDarkTheme.RaisedSurface : MinimalDarkTheme.Surface);
        graphics.FillRectangle(fill, fillBounds);
        var centerX = arrow.Left + arrow.Width / 2;
        var centerY = arrow.Top + arrow.Height / 2;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var chevron = new Pen(Enabled ? MinimalDarkTheme.Text : MinimalDarkTheme.MutedText, Math.Max(1f, DeviceDpi / 96f * 1.5f))
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            graphics.DrawLines(chevron, new Point[] { new(centerX - Scale(4), centerY - Scale(2)),
                new(centerX, centerY + Scale(2)), new(centerX + Scale(4), centerY - Scale(2)) });
        }
        graphics.SmoothingMode = SmoothingMode.None;
        using var border = new Pen(Enabled && Focused ? MinimalDarkTheme.Accent : MinimalDarkTheme.Border);
        graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ComboBoxInfo
    {
        public int Size;
        public NativeRectangle Item, Button;
        public uint ButtonState;
        public IntPtr Combo, ItemWindow, ListWindow;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetComboBoxInfo(IntPtr combo, ref ComboBoxInfo info);
}

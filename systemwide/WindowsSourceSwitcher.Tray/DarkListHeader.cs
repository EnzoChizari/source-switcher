using System.Runtime.InteropServices;

namespace WindowsSourceSwitcher.Tray;

internal sealed class DarkListHeader : NativeWindow, IDisposable
{
    private readonly ListView _list;

    private DarkListHeader(ListView list, nint header)
    {
        _list = list;
        AssignHandle(header);
    }

    public static DarkListHeader? Attach(ListView list)
    {
        var header = SendMessage(list.Handle, 0x101F /* LVM_GETHEADER */, 0, 0);
        return header == 0 ? null : new DarkListHeader(list, header);
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (Handle == 0 || SystemInformation.HighContrast) return;
        if (message.Msg == 0x000F /* WM_PAINT */)
            PaintRemainder(0);
        else if (message.Msg == 0x0318 /* WM_PRINTCLIENT */ && message.WParam != 0)
            PaintRemainder(message.WParam);
    }

    private void PaintRemainder(nint suppliedDc)
    {
        var header = Handle;
        if (!GetClientRect(header, out var client) || client.Right <= 0 || client.Bottom <= 0) return;
        var right = 0;
        for (var index = 0; index < _list.Columns.Count; index++)
        {
            if (SendMessage(header, 0x1207 /* HDM_GETITEMRECT */, index, out var item) != 0)
                right = Math.Max(right, item.Right);
        }
        if (right >= client.Right) return;

        // The native header paints beyond the last column separately from owner-drawn cells.
        var dc = suppliedDc != 0 ? suppliedDc : GetDC(header);
        if (dc == 0) return;
        try
        {
            using var graphics = Graphics.FromHdc(dc);
            using var background = new SolidBrush(MinimalDarkTheme.Background);
            graphics.FillRectangle(background, right, 0, client.Right - right, client.Bottom);
            using var border = new Pen(MinimalDarkTheme.Border);
            graphics.DrawLine(border, right, client.Bottom - 1, client.Right - 1, client.Bottom - 1);
        }
        finally
        {
            if (suppliedDc == 0) ReleaseDC(header, dc);
        }
    }

    public void Dispose()
    {
        if (Handle != 0) ReleaseHandle();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, int message, nint wParam, out NativeRect lParam);
}

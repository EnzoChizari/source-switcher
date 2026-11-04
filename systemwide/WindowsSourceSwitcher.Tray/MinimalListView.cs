namespace WindowsSourceSwitcher.Tray;

/// <summary>Keep the native disabled list and its accessibility semantics, while
/// painting unused viewport space with the same palette as the owner-drawn rows.</summary>
internal sealed class MinimalListView : ListView
{
    protected override void WndProc(ref Message message)
    {
        // Native disabled-background erasure otherwise uses the light system color.
        // Handle the supplied DC for both normal painting and WM_PRINT/DrawToBitmap.
        if (message.Msg == 0x0014 /* WM_ERASEBKGND */ && !Enabled
            && !IsDisposed && !Disposing && !SystemInformation.HighContrast && message.WParam != IntPtr.Zero)
        {
            using var graphics = Graphics.FromHdc(message.WParam);
            using var background = new SolidBrush(BackColor);
            graphics.FillRectangle(background, ClientRectangle);
            message.Result = (IntPtr)1;
            return;
        }
        base.WndProc(ref message);
    }
}

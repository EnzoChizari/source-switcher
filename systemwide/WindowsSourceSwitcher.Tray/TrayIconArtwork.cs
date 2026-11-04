using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace WindowsSourceSwitcher.Tray;

internal static class TrayIconArtwork
{
    internal static Icon CreateForCurrentTheme(bool updateAvailable = false)
    {
        var lightTaskbar = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            lightTaskbar = key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        if (SystemInformation.HighContrast) lightTaskbar = SystemColors.Window.GetBrightness() > .5f;
        return Create(lightTaskbar, updateAvailable);
    }

    internal static Icon Create(bool lightTaskbar, bool updateAvailable = false)
    {
        var resource = "WindowsSourceSwitcher.Tray.Assets.TrayMixer" + (lightTaskbar ? "Black" : "White") + ".ico";
        using var stream = typeof(TrayIconArtwork).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Tray icon resource is missing.");
        using var original = new Icon(stream, SystemInformation.SmallIconSize);
        if (!updateAvailable) return (Icon)original.Clone();

        using var baseImage = original.ToBitmap();
        using var image = new Bitmap(original.Width, original.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(image))
        {
            var side = Math.Min(image.Width, image.Height);
            var badgeSize = Math.Max(5, (int)Math.Round(side * .44));
            var baseSize = side - Math.Max(2, badgeSize / 3);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(baseImage, new Rectangle(0, image.Height - baseSize, baseSize, baseSize));
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var badgeLeft = image.Width - badgeSize;
            using var fill = new SolidBrush(MinimalDarkTheme.Accent);
            graphics.FillEllipse(fill, badgeLeft, 0, badgeSize, badgeSize);

            // A pixel-aligned glyph stays legible at the Windows 16-pixel tray size.
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            var stroke = Math.Max(1, badgeSize / 6);
            var center = badgeLeft + (badgeSize - stroke) / 2;
            graphics.FillRectangle(Brushes.Black, center, badgeSize / 4, stroke, Math.Max(2, badgeSize / 3));
            graphics.FillRectangle(Brushes.Black, center, badgeSize - 1 - stroke, stroke, stroke);
        }
        var handle = image.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}

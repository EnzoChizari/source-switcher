using Microsoft.Win32;

namespace WindowsSourceSwitcher.Tray;

internal static class TrayIconArtwork
{
    internal static Icon CreateForCurrentTheme()
    {
        var lightTaskbar = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            lightTaskbar = key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        if (SystemInformation.HighContrast) lightTaskbar = SystemColors.Window.GetBrightness() > .5f;
        return Create(lightTaskbar);
    }

    internal static Icon Create(bool lightTaskbar)
    {
        var resource = "WindowsSourceSwitcher.Tray.Assets.TrayMixer" + (lightTaskbar ? "Black" : "White") + ".ico";
        using var stream = typeof(TrayIconArtwork).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Tray icon resource is missing.");
        using var original = new Icon(stream, SystemInformation.SmallIconSize);
        return (Icon)original.Clone();
    }
}

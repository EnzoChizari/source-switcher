namespace WindowsSourceSwitcher.Tray;

internal static class TrayIconActivation
{
    internal static void OpenOnLeftClick(NotifyIcon icon, Action open)
    {
        ArgumentNullException.ThrowIfNull(icon);
        ArgumentNullException.ThrowIfNull(open);
        // Opening is idempotent: a visible window may be behind another application.
        // The first click must bring it forward instead of hiding it.
        icon.MouseClick += (_, args) => { if (args.Button == MouseButtons.Left) open(); };
    }
}

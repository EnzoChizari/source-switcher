namespace WindowsSourceSwitcher.Tray;

internal sealed class StartupMenuItem
{
    private readonly IStartupRegistration registration;
    private readonly Action<string> showError;
    private bool busy;
    internal ToolStripMenuItem Item { get; } = new("Start with Windows") { CheckOnClick = false };
    internal StartupMenuItem(ContextMenuStrip menu, IStartupRegistration registration, Action<string> showError)
    {
        this.registration = registration; this.showError = showError;
        menu.Opening += async (_, _) => await RefreshAsync();
        Item.Click += async (_, _) => await ToggleAsync();
    }
    internal async Task RefreshAsync()
    {
        if (busy || Item.IsDisposed) return;
        busy = true; Item.Enabled = false;
        try
        {
            var enabled = await Task.Run(registration.ReadEnabled);
            if (Item.IsDisposed) return;
            Item.Checked = enabled;
            Item.ToolTipText = "Start minimized when you sign in to Windows."; Item.Enabled = true;
        }
        catch (Exception ex) { if (!Item.IsDisposed) Item.ToolTipText = "Startup status unavailable: " + ex.Message; }
        finally { busy = false; }
    }
    internal async Task ToggleAsync()
    {
        if (busy || Item.IsDisposed) return;
        busy = true; Item.Enabled = false;
        var enable = !Item.Checked;
        string? error = null;
        try
        {
            var enabled = await Task.Run(() => { registration.SetEnabled(enable); return registration.ReadEnabled(); });
            if (!Item.IsDisposed) Item.Checked = enabled;
        }
        catch (Exception ex) { error = "Could not change Start with Windows: " + ex.Message; }
        finally { busy = false; }
        if (Item.IsDisposed) return;
        await RefreshAsync();
        if (error is not null) showError(error);
    }
}

using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using WindowsSourceSwitcher.Tray;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class TrayIconActivationTests
{
    [Fact]
    public void One_left_click_opens_hidden_popup_and_another_does_not_hide_it() => OnSta(() =>
    {
        using var icon = new NotifyIcon();
        using var popup = new SourcePopup { Opacity = 0 };
        TrayIconActivation.OpenOnLeftClick(icon, popup.ShowAtTray);
        Assert.False(popup.Visible);
        Click(icon, MouseButtons.Left);
        Assert.True(popup.Visible);
        Click(icon, MouseButtons.Left);
        Assert.True(popup.Visible);
        popup.Hide();
        Click(icon, MouseButtons.Left);
        Assert.True(popup.Visible);
    });

    [Theory]
    [InlineData(MouseButtons.Right)]
    [InlineData(MouseButtons.Middle)]
    [InlineData(MouseButtons.None)]
    public void Nonleft_click_keeps_popup_hidden(MouseButtons button) => OnSta(() =>
    {
        using var icon = new NotifyIcon();
        using var popup = new SourcePopup { Opacity = 0 };
        TrayIconActivation.OpenOnLeftClick(icon, popup.ShowAtTray);
        Click(icon, button);
        Assert.False(popup.Visible);
    });

    private static void Click(NotifyIcon icon, MouseButtons button) =>
        typeof(NotifyIcon).GetMethod("OnMouseClick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(icon, [new MouseEventArgs(button, 1, 0, 0, 0)]);

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Tray click test did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

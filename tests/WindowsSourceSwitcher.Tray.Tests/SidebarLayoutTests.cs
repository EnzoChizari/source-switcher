using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class SidebarLayoutTests
{
    [Fact]
    public void SidebarAndKeyboardNavigationShareSelectionWithoutAudioOrPreferenceWrites() => OnSta(() =>
    {
        using var popup = new SourcePopup { Opacity = 0 };
        var settings = new SourceSettings();
        var writes = 0;
        popup.SourceSelected += _ => writes++;
        popup.RateSelected += _ => writes++;
        popup.SwitchingEnabledChanged += _ => writes++;
        popup.Render([], [], [], null, settings, "Status", "Evidence", false);
        popup.Show(); Application.DoEvents();
        var tabs = Find<TabControl>(popup).Single();
        var navigation = Find<SidebarButton>(popup).OrderBy(button => button.Section).ToArray();
        Assert.Equal(4, navigation.Length);
        Assert.Equal(0, tabs.SelectedIndex);
        Assert.True(navigation[0].Selected);
        for (var index = 1; index < 4; index++)
        {
            navigation[index].PerformClick(); Application.DoEvents();
            Assert.Equal(index, tabs.SelectedIndex);
            Assert.Single(navigation, button => button.Selected);
            Assert.True(navigation[index].Selected);
            Assert.Equal(index == 1, popup.MixerVisible);
            Assert.Equal(index == 3, popup.DiagnosticsVisible);
        }
        var message = Message.Create(popup.Handle, 0x100, (nint)Keys.Tab, 0);
        Assert.True((bool)typeof(SourcePopup).GetMethod("ProcessCmdKey", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(popup, [message, Keys.Control | Keys.Tab])!);
        Assert.Equal(0, tabs.SelectedIndex);
        Assert.Equal(0, writes);
    });

    [Theory]
    [InlineData(560, 640)]
    [InlineData(680, 740)]
    [InlineData(1000, 1000)]
    public void NavigationStaysReachableInConstrainedAndExpandedLayouts(int width, int height) => OnSta(() =>
    {
        using var popup = new SourcePopup { ClientSize = new Size(width, height), Opacity = 0 };
        popup.Show(); Application.DoEvents();
        var navigation = Find<SidebarButton>(popup).ToArray();
        foreach (var button in navigation)
        {
            Assert.True(button.Visible);
            Assert.True(button.Enabled);
            Assert.True(button.TabStop);
            Assert.True(button.Width >= 40);
            Assert.True(popup.ClientRectangle.Contains(popup.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))));
            Assert.Equal(width < 600, button.Compact);
        }
        Assert.DoesNotContain(Find<Control>(popup), control => control.GetType().Name.Contains("WebView"));
    });

    [Theory]
    [InlineData(1f)]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void ShortScreenKeepsAllNavigationReachableAfterScalingAndRestoresFooter(float scale) => OnSta(() =>
    {
        using var popup = new SourcePopup { Opacity = 0 };
        popup.Show(); Application.DoEvents();
        popup.Scale(new SizeF(scale, scale));
        popup.FitToWorkingArea(new Rectangle(-800, 0, 800, 450));
        Application.DoEvents();
        var version = Find<Label>(popup).Single(label => label.Text.StartsWith("Audio control\n"));
        var navigation = Find<SidebarButton>(popup).OrderBy(button => button.Section).ToArray();
        var tabs = Find<TabControl>(popup).Single();
        foreach (var button in navigation)
        {
            button.Focus(); Application.DoEvents();
            Assert.True(popup.ClientRectangle.Contains(popup.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))));
            button.PerformClick();
            Assert.Equal(button.Section, tabs.SelectedIndex);
        }
        popup.FitToWorkingArea(new Rectangle(0, 0, 1920, 1080));
        Application.DoEvents();
        Assert.True(version.Visible);
    });

    private static IEnumerable<T> Find<T>(Control parent) where T : Control => parent.Controls.Cast<Control>()
        .SelectMany(child => (child is T match ? new[] { match } : []).Concat(Find<T>(child)));
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20))) throw new TimeoutException("Sidebar layout did not settle.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

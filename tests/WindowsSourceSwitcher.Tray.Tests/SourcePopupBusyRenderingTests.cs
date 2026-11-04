using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;
namespace WindowsSourceSwitcher.Tray.Tests;
public sealed class SourcePopupBusyRenderingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void SourceViewportStaysDarkThroughBusyAndIdleWithoutSelectionActions(int count) => OnSta(() =>
    {
        if (SystemInformation.HighContrast) return;
        using var popup = new SourcePopup { ClientSize = new Size(640, 820) };
        var sources = Enumerable.Range(0, count).Select(index => new PlatformAudioSource(
            SourceIdentity.FromExecutablePath(@"C:\Apps\player" + index + ".exe"), "Player " + index,
            @"C:\Apps\player" + index + ".exe", [123 + index], true, true, "session" + index)).ToArray();
        var settings = new SourceSettings();
        if (sources.Length > 0) settings.SelectMaster(sources[0].Identity, sources[0].ProcessName);
        var events = 0;
        popup.SourceSelected += _ => events++;
        void Render(bool busy) => popup.Render([], sources, [], null, settings, "Switching source", "Evidence", busy);
        Render(false); popup.Show(); Application.DoEvents();
        var list = Find<ListView>(popup).Single(list => list.AccessibleName == "Audio sources; star marks favorites");
        var selected = list.SelectedItems.Cast<ListViewItem>().Select(item => item.Text).ToArray();
        for (var index = 0; index < 3; index++)
        {
            Render(true); Application.DoEvents();
            Assert.False(IsWindowEnabled(list.Handle));
            AssertSurface(list, "busy");
            Assert.True((list.AccessibilityObject.State & AccessibleStates.Unavailable) != 0);
            if (index == 1)
            {
                typeof(Control).GetMethod("RecreateHandle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(list, null);
                Application.DoEvents();
                Assert.False(IsWindowEnabled(list.Handle));
                AssertSurface(list, "busy recreated handle");
            }
            Render(false); Application.DoEvents();
            Assert.True(IsWindowEnabled(list.Handle));
            AssertSurface(list, "idle");
            Assert.Equal(selected, list.SelectedItems.Cast<ListViewItem>().Select(item => item.Text).ToArray());
        }
        Assert.Equal(0, events);
    });
    private static void AssertSurface(ListView list, string state)
    {
        using var bitmap = new Bitmap(list.ClientSize.Width, list.ClientSize.Height);
        list.DrawToBitmap(bitmap, list.ClientRectangle);
        var actual = bitmap.GetPixel(list.ClientSize.Width / 2, list.ClientSize.Height - 8);
        Assert.True(actual.ToArgb() == MinimalDarkTheme.Surface.ToArgb(), $"{state} empty viewport: {actual}; expected {MinimalDarkTheme.Surface}.");
    }
    private static IEnumerable<T> Find<T>(Control parent) where T : Control => parent.Controls.Cast<Control>()
        .SelectMany(child => (child is T match ? new[] { match } : []).Concat(Find<T>(child)));
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Busy rendering test timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
}



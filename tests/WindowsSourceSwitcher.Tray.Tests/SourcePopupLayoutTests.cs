using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WindowsLosslessSwitcher.Models;
using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class SourcePopupLayoutTests
{
    [Fact]
    public void EnlargedStartupTextFitsOutputSummaryAndSourceControls() => OnSta(() =>
    {
        using var popup = new SourcePopup { Opacity = 0 };
        var summary = Find<Label>(popup).Single(label => label.Text.StartsWith("Output:"));
        using var enlarged = new Font(popup.Font.FontFamily, 14.25f);
        using var bold = new Font(summary.Font.FontFamily, 19.5f, FontStyle.Bold);
        popup.Scale(new SizeF(1.5f, 1.5f));
        popup.Font = enlarged;
        summary.Font = bold;
        popup.ClientSize = new Size(1020, 1110);
        const string key = @"C:\Apps\Discord.exe";
        var settings = new SourceSettings { TargetEndpointId = "dac" };
        settings.SelectMaster(key, "Discord");
        settings.SetPinned(key, "Discord", true);
        var devices = new AudioDeviceInfo[] { new("dac", "Test USB DAC", true) };
        popup.Render(devices, [], [], new AudioFormatCandidate(48000, 32, 2), settings, "Status", "Evidence", false);
        popup.Show();
        Application.DoEvents();

        Assert.Equal(summary.PreferredHeight, summary.Height);
        Assert.True(summary.Parent!.ClientRectangle.Contains(summary.Bounds), $"Output summary {summary.Bounds} extends past {summary.Parent.ClientRectangle}.");
        var pin = Find<Button>(popup).Single(button => button.Text == "Unpin");
        Assert.True(pin.Parent!.ClientRectangle.Contains(pin.Bounds));
        Assert.True(pin.Width >= TextRenderer.MeasureText(pin.Text, pin.Font).Width);
        foreach (var button in Find<Button>(popup).Where(button => button.Text is "Refresh / retry" or "Release master"))
            Assert.True(button.Parent!.ClientRectangle.Contains(button.Bounds), $"{button.Text} is clipped.");
        var sources = Find<ListView>(popup).Single(list => list.AccessibleName == "Audio sources; star marks favorites");
        Assert.True(sources.Columns[1].Width >= TextRenderer.MeasureText("Idle / paused", sources.Font).Width + 16);
        var output = Find<ComboBox>(popup).Single(combo => combo.Items.Count == 2);
        Assert.Contains("Test USB DAC", output.Text);
        var changes = 0;
        popup.DeviceSelected += _ => changes++;
        output.SelectedIndex = 0;
        popup.Render(devices, [], [], new AudioFormatCandidate(48000, 32, 2), settings, "Status", "Evidence", false);
        Assert.Contains("Test USB DAC", output.Text);
        Assert.Equal(0, changes);
    });

    [Fact]
    public void HeaderHideButtonKeepsTheReusablePopupAndMasterAlive() => OnSta(() =>
    {
        using var popup = new SourcePopup();
        var releases = 0;
        popup.ReleaseRequested += () => releases++;
        var hide = Find<Button>(popup).Single(button => button.AccessibleName == "Hide to tray");
        for (var index = 0; index < 2; index++)
        {
            popup.Show();
            hide.PerformClick();
            Assert.False(popup.Visible);
            Assert.False(popup.IsDisposed);
        }
        Assert.Equal(0, releases);
    });

    [Fact]
    public void WindowsOnlyControlsDoNotExposeTheWithdrawnBrowserIntegration() => OnSta(() =>
    {
        using var popup = new SourcePopup();
        Assert.DoesNotContain(Find<CheckBox>(popup), control => control.AccessibleName == "Use pinned browser tab rate");
        Assert.DoesNotContain(Find<Button>(popup), control => control.AccessibleName == "Register browser helper");
        Assert.Equal(FormBorderStyle.None, popup.FormBorderStyle);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repeated_native_close_requests_hide_the_popup_and_allow_reopening(bool initiallyHidden) => OnSta(() =>
    {
        using var popup = new SourcePopup { Opacity = 0 };
        popup.ShowAtTray();
        if (initiallyHidden) popup.Hide();
        for (var index = 0; index < 3; index++)
        {
            SendMessage(popup.Handle, 0x10, 0, 0);
            Assert.False(popup.IsDisposed);
            Assert.False(popup.Visible);
            popup.ShowAtTray();
            Assert.True(popup.Visible);
        }
    });

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    [Fact]
    public void CompactSourcesKeepsDetectionPreferencesInControlsWithoutChangingSettings() => OnSta(() =>
    {
        using var popup = new SourcePopup();
        var settings = new SourceSettings { StreamRateDetectionEnabled = true, CatalogRateDetectionEnabled = true };
        var writes = 0;
        popup.StreamDetectionChanged += _ => writes++;
        popup.CatalogChanged += _ => writes++;
        popup.Render([], [], [], null, settings, "Status", "Evidence", false);
        var checkboxes = Find<CheckBox>(popup).ToArray();
        var stream = checkboxes.Single(c => c.Text == "Auto-detect application stream rate");
        var catalog = checkboxes.Single(c => c.Text == "Use Apple Music catalog rate (estimate)");
        Assert.True(Find<ControlsSettingsView>(popup).Single().Contains(stream));
        Assert.Same(stream.Parent, catalog.Parent);
        Assert.True(stream.Checked);
        Assert.True(catalog.Checked);
        Assert.Equal(0, writes);
        Assert.Equal(740, popup.ClientSize.Height);
    });

    [Fact]
    public void SmallScreenKeepsPopupOnScreenAndSourcesScrollableThenRestoresNormalSize() => OnSta(() =>
    {
        using var popup = new SourcePopup();
        var area = new Rectangle(-900, 0, 800, 450);
        popup.FitToWorkingArea(area);
        _ = popup.Handle;
        popup.PerformLayout();
        Assert.True(area.Contains(popup.Bounds));
        var sources = Find<ListView>(popup).Single(list => list.AccessibleName == "Audio sources; star marks favorites");
        var viewport = Find<Panel>(popup).Single(panel => panel.AutoScroll && panel.Contains(sources));
        Assert.True(viewport.AutoScroll);
        Assert.True(viewport.Controls[0].Height > viewport.ClientSize.Height);

        popup.FitToWorkingArea(new Rectangle(0, 0, 1920, 1080));
        Assert.Equal(new Size(680, 740), popup.ClientSize);
    });

    [Fact]
    public void EscapeHidesWithoutReleasingMasterOrDisposingPopup() => OnSta(() =>
    {
        using var popup = new SourcePopup();
        var releases = 0;
        popup.ReleaseRequested += () => releases++;
        popup.Show();
        var message = Message.Create(popup.Handle, 0x100, (IntPtr)Keys.Escape, IntPtr.Zero);
        var handled = (bool)typeof(SourcePopup).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(popup, [message, Keys.Escape])!;
        Assert.True(handled);
        Assert.False(popup.Visible);
        Assert.False(popup.IsDisposed);
        Assert.Equal(0, releases);
    });

    private static IEnumerable<T> Find<T>(Control control) where T : Control =>
        control.Controls.Cast<Control>().SelectMany(child => (child is T match ? new[] { match } : []).Concat(Find<T>(child)));

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Popup layout test did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class SourcePopupEvidenceTests
{
    [Fact]
    public void Browser_master_does_not_hide_or_freeze_Apple_track_metadata() => OnSta(() =>
    {
        using var popup = new SourcePopup();
        const string applePath = @"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.2.3.4_x64__nzyj5cx40ttqa\AppleMusic.exe";
        var apple = new PlatformAudioSource(applePath, "Apple Music", applePath, [10], true, true, "apple");
        var browser = new PlatformAudioSource(@"C:\Apps\zen.exe", "Zen", @"C:\Apps\zen.exe", [20], true, true, "zen");
        var settings = new SourceSettings { CatalogRateDetectionEnabled = true, BrowserRateDetectionEnabled = true };
        settings.SelectMaster(browser.Identity, browser.ProcessName);
        var decoder = new RateObservation(browser.Identity, browser.SessionToken, settings.SelectionRevision,
            48000, RateProvenance.BrowserDecoder, "Current chosen tab decoder");
        var catalog = new RateObservation(apple.Identity, apple.SessionToken, settings.SelectionRevision,
            96000, RateProvenance.CatalogMetadata, "Current Apple track");
        void Render(RateObservation? currentApple) => popup.Render([], [apple, browser], [], null, settings,
            "Status", "Evidence", false, observation: decoder, appleCatalogObservation: currentApple);
        string Rate(string name) => Find<ListView>(popup).SelectMany(view => view.Items.Cast<ListViewItem>())
            .Single(row => row.SubItems.Count == 3 && row.Text == name).SubItems[2].Text;

        Render(catalog);
        Assert.Equal("48 kHz tab decoder", Rate("Zen"));
        Assert.Equal("96 kHz est.", Rate("Apple Music"));
        Render(catalog with { SampleRateHz = 192000 });
        Assert.Equal("192 kHz est.", Rate("Apple Music"));
        Render(null);
        Assert.Equal("Estimate pending", Rate("Apple Music"));
        Assert.Equal("48 kHz tab decoder", Rate("Zen"));
    });

    private static IEnumerable<T> Find<T>(Control control) where T : Control =>
        control.Controls.Cast<Control>().SelectMany(child => (child is T match ? new[] { match } : []).Concat(Find<T>(child)));

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Source evidence UI test did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

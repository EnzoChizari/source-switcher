using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class BrowserSourcePopupInvalidationTests
{
    [Fact]
    public void CapabilityStateAndOptInChangesRefreshRowsWithoutAnyDecoderObservation() => OnSta(() =>
    {
        const string path = @"C:\Program Files\Zen Browser\zen.exe";
        var source = new PlatformAudioSource(SourceIdentity.FromExecutablePath(path), "Zen", path,
            [100, 200], true, true, "current-session");
        var settings = new SourceSettings { BrowserRateDetectionEnabled = true };
        settings.SelectMaster(source.Identity, source.ProcessName);
        settings.RememberAppliedRate(source.Identity, 44100);
        var revision = settings.SelectionRevision;
        var stream = new RateObservation(source.Identity, source.SessionToken, revision, 48000,
            RateProvenance.WasapiStream, "Actual submitted PCM");
        var streams = new Dictionary<string, RateObservation> { [source.Identity] = stream };
        var browser = new BrowserDecoderSnapshot(1, source.Identity, 100,
            DateTimeOffset.Parse("2026-10-20T00:00:00Z"), true, 7, "document", 1, false, null, null,
            BrowserDecoderState.Unavailable, "Supported adapter temporarily unavailable", 1234, "browser", "Tab");
        using var popup = new SourcePopup();
        var changes = new List<string>();
        popup.SourceSelected += _ => changes.Add("source");
        popup.RateSelected += _ => changes.Add("rate");
        popup.VolumeChangeRequested += _ => changes.Add("volume");

        void Render(BrowserDecoderSnapshot snapshot) => popup.Render([], [source], [], null, settings,
            "Status", "Evidence", applying: false, observation: null, streamObservations: streams, browser: snapshot);
        ListViewItem Row() => Assert.Single(Find<ListView>(popup)
            .Single(list => list.AccessibleName == "Audio sources; star marks favorites").Items.Cast<ListViewItem>());

        Render(browser);
        Assert.Equal("44.1 kHz held", Row().SubItems[2].Text);
        Assert.Contains(browser.Detail, Row().ToolTipText);

        // Same source, current sessions, revision, held target and Windows PCM evidence:
        // only the browser semantic version/capability changes.
        browser = browser with { Version = 2, DecoderSupported = false, Detail = "Decoder adapter unsupported" };
        Render(browser);
        Assert.Equal("48 kHz stream", Row().SubItems[2].Text);
        Assert.Contains("Actual submitted PCM", Row().ToolTipText);

        browser = browser with { Version = 3, DecoderSupported = true, State = BrowserDecoderState.Disconnected,
            Detail = "Browser disconnected; holding confirmed rate" };
        Render(browser);
        Assert.Equal("44.1 kHz held", Row().SubItems[2].Text);
        Assert.Contains(browser.Detail, Row().ToolTipText);

        // A state change can update diagnostics while the numeric held label stays identical.
        browser = browser with { Version = 4, State = BrowserDecoderState.TimedOut,
            Detail = "Decoder request timed out; holding confirmed rate" };
        Render(browser);
        Assert.Equal("44.1 kHz held", Row().SubItems[2].Text);
        Assert.Contains(browser.Detail, Row().ToolTipText);

        settings.BrowserRateDetectionEnabled = false;
        Render(browser);
        Assert.Equal("48 kHz stream", Row().SubItems[2].Text);
        Assert.Empty(changes);
        Assert.Equal(revision, settings.SelectionRevision);
        Assert.Equal(44100, settings.LastKnownSampleRateHz);
    });

    private static IEnumerable<T> Find<T>(Control control) where T : Control =>
        control.Controls.Cast<Control>().SelectMany(child => (child is T match ? new[] { match } : []).Concat(Find<T>(child)));
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Browser popup test did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

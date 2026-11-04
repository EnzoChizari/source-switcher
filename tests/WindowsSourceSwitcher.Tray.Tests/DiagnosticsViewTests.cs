using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WindowsSourceSwitcher.Tray;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class DiagnosticsViewTests
{
    [Fact]
    public void Rendering_is_read_only_and_does_not_copy_until_user_clicks() => OnSta(() =>
    {
        var copies = new List<string>();
        using var view = new DiagnosticsView(copies.Add);
        var snapshot = Snapshot();

        view.Render(snapshot);
        view.Render(snapshot);

        Assert.Empty(copies);
        Assert.Empty(Find<TrackBar>(view));
        Assert.Empty(Find<CheckBox>(view));
        Assert.All(Find<ListView>(view), list => Assert.False(list.LabelEdit));
        Click(Find<Button>(view).Single());
        Assert.Equal(DiagnosticsPresentation.FormatSnapshot(snapshot), Assert.Single(copies));
        Assert.Equal("Diagnostics copied.", CopyStatus(view).Text);
    });

    [Fact]
    public void Same_snapshot_preserves_row_instances_and_copy_status() => OnSta(() =>
    {
        using var view = new DiagnosticsView(_ => { });
        view.Render(Snapshot());
        var field = Fields(view).Items[0];
        var history = History(view).Items[0];
        Click(Find<Button>(view).Single());

        view.Render(Snapshot());

        Assert.Same(field, Fields(view).Items[0]);
        Assert.Same(history, History(view).Items[0]);
        Assert.Equal("Diagnostics copied.", CopyStatus(view).Text);
    });

    [Fact]
    public void Changed_field_updates_in_place_and_full_tooltip_matches_latest_value() => OnSta(() =>
    {
        using var view = new DiagnosticsView(_ => { });
        view.Render(Snapshot());
        var row = Fields(view).Items[0];
        var updated = new AudioDiagnosticsSnapshot([new("Native rate", "96 kHz — decoder reported"), new("Submitted PCM", "48 kHz")], []);

        view.Render(updated);

        Assert.Same(row, Fields(view).Items[0]);
        Assert.Equal("96 kHz — decoder reported", row.SubItems[1].Text);
        Assert.Equal("Native rate: 96 kHz — decoder reported", row.ToolTipText);
        Assert.Empty(History(view).Items.Cast<ListViewItem>());
    });

    [Fact]
    public void History_reuses_existing_sequence_rows_and_shows_newest_first() => OnSta(() =>
    {
        using var view = new DiagnosticsView(_ => { });
        var original = Snapshot();
        view.Render(original);
        var row = History(view).Items[0];
        var next = new ControlHistoryEntry(2, DateTimeOffset.UnixEpoch.AddSeconds(1), "Format result", "Readback matched.");

        view.Render(new(original.Fields, [next, original.History[0]]));

        Assert.Equal("Format result", History(view).Items[0].SubItems[1].Text);
        Assert.Same(row, History(view).Items[1]);
        Assert.Contains("1970-01-01T00:00:01.0000000+00:00", History(view).Items[0].ToolTipText);
    });

    [Fact]
    public void Copy_exports_the_displayed_snapshot_even_if_caller_later_mutates_its_list() => OnSta(() =>
    {
        var copies = new List<string>();
        using var view = new DiagnosticsView(copies.Add);
        var fields = new List<DiagnosticField> { new("Submitted PCM", "48 kHz") };
        var history = new List<ControlHistoryEntry> { new(1, DateTimeOffset.UnixEpoch, "Format attempt", "Requested 48 kHz.") };
        view.Render(new(fields, history));
        fields[0] = new("Submitted PCM", "192 kHz");
        history.Clear();

        Click(Find<Button>(view).Single());

        var text = Assert.Single(copies);
        Assert.Contains("Submitted PCM: 48 kHz", text);
        Assert.Contains("Requested 48 kHz.", text);
        Assert.DoesNotContain("192 kHz", text);
    });

    [Fact]
    public void Empty_snapshot_disables_copy_and_clears_previous_fields_and_history() => OnSta(() =>
    {
        var copies = new List<string>();
        using var view = new DiagnosticsView(copies.Add);
        Assert.False(Find<Button>(view).Single().Enabled);
        view.Render(Snapshot());
        Assert.True(Find<Button>(view).Single().Enabled);

        view.Render(new([], []));
        Click(Find<Button>(view).Single());

        Assert.False(Find<Button>(view).Single().Enabled);
        Assert.Empty(copies);
        Assert.Empty(Fields(view).Items.Cast<ListViewItem>());
        Assert.Empty(History(view).Items.Cast<ListViewItem>());
    });

    [Fact]
    public void Clipboard_failure_is_visible_and_next_explicit_copy_can_retry() => OnSta(() =>
    {
        var attempts = 0;
        using var view = new DiagnosticsView(_ =>
        {
            if (++attempts == 1) throw new ExternalException("Clipboard busy.");
        });
        view.Render(Snapshot());

        Click(Find<Button>(view).Single());
        Assert.Contains("Clipboard unavailable", CopyStatus(view).Text);
        Assert.Equal("Unknown", Fields(view).Items[0].SubItems[1].Text);
        Click(Find<Button>(view).Single());

        Assert.Equal(2, attempts);
        Assert.Equal("Diagnostics copied.", CopyStatus(view).Text);
    });

    [Fact]
    public void Hidden_popup_does_not_rebuild_diagnostics_and_navigation_emits_no_audio_actions() => OnSta(() =>
    {
        using var popup = new SourcePopup();
        var audioActions = 0;
        popup.SourceSelected += _ => audioActions++;
        popup.SavedSourceSelected += (_, _) => audioActions++;
        popup.RateSelected += _ => audioActions++;
        popup.VolumeChangeRequested += _ => audioActions++;
        _ = popup.Handle;

        popup.SelectDiagnosticsTab();
        popup.RenderDiagnostics(Snapshot());

        var view = Find<DiagnosticsView>(popup).Single();
        Assert.False(popup.DiagnosticsVisible);
        Assert.False(popup.Visible);
        Assert.Empty(Fields(view).Items.Cast<ListViewItem>());
        Assert.Equal(0, audioActions);
    });

    [Fact]
    public void Visible_diagnostics_tab_renders_and_leaving_it_preserves_last_display() => OnSta(() =>
    {
        using var popup = new SourcePopup { Opacity = 0, Location = new System.Drawing.Point(-20000, -20000) };
        popup.SelectDiagnosticsTab();
        popup.Show();
        Assert.True(popup.DiagnosticsVisible);
        Assert.False(popup.MixerVisible);
        popup.RenderDiagnostics(Snapshot());
        var view = Find<DiagnosticsView>(popup).Single();
        var row = Fields(view).Items[0];
        popup.SelectSourcesTab();

        popup.RenderDiagnostics(new([new("Native rate", "Unexpected hidden update")], []));

        Assert.False(popup.DiagnosticsVisible);
        Assert.Same(row, Fields(view).Items[0]);
        Assert.Equal("Unknown", row.SubItems[1].Text);
        popup.Hide();
    });

    private static AudioDiagnosticsSnapshot Snapshot() => new([
        new("Native rate", "Unknown"), new("Submitted PCM", "48 kHz")],
        [new(1, DateTimeOffset.UnixEpoch, "Format attempt", "Requested 48 kHz.")]);
    private static ListView Fields(Control parent) => Find<ListView>(parent).Single(list => list.AccessibleName == "Current audio diagnostics");
    private static ListView History(Control parent) => Find<ListView>(parent).Single(list => list.AccessibleName == "Controller event history");
    private static Label CopyStatus(Control parent) => Find<Label>(parent).Single(label => label.AccessibleName == "Diagnostics copy status");
    private static void Click(Button button) => typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(button, [EventArgs.Empty]);
    private static IEnumerable<T> Find<T>(Control control) where T : Control =>
        control.Controls.Cast<Control>().SelectMany(child => (child is T match ? new[] { match } : []).Concat(Find<T>(child)));

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Diagnostics UI test did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

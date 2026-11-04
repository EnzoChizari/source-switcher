using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using WindowsSourceSwitcher.Tray;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class VolumeMixerViewTests
{
    [Fact]
    public void Opening_and_external_refresh_read_values_without_requesting_any_write() => OnSta(() =>
    {
        using var view = new VolumeMixerView();
        var changes = new List<VolumeMixerChange>();
        view.ChangeRequested += changes.Add;
        view.Render(Census(Source(.35f)));
        Assert.Equal(35, Slider(view).Value);
        view.Render(Census(Source(.72f, muted: true)));
        Assert.Equal(72, Slider(view).Value);
        Assert.True(Find<CheckBox>(view).Single().Checked);
        Assert.Empty(changes);
    });

    [Fact]
    public void Older_read_or_write_completion_cannot_move_a_newer_user_choice_back() => OnSta(() =>
    {
        using var view = new VolumeMixerView();
        var changes = new List<VolumeMixerChange>();
        view.ChangeRequested += changes.Add;
        view.Render(Census(Source(.25f)));
        Slider(view).Value = 40;
        var first = Assert.Single(changes);
        Slider(view).Value = 65;
        Assert.Equal(2, changes.Count);
        view.Render(Census(Source(.25f)));
        Assert.Equal(65, Slider(view).Value);
        view.CompleteWrite(first, new(true, 1, 0, null, Source(.40f)));
        Assert.Equal(65, Slider(view).Value);
        view.CompleteWrite(changes[1], new(true, 1, 0, null, Source(.65f)));
        view.Render(Census(Source(.30f)));
        Assert.Equal(30, Slider(view).Value);
        Assert.Equal(2, changes.Count);
    });

    [Fact]
    public void Volume_and_mute_emit_only_the_app_session_snapshot_the_user_touched() => OnSta(() =>
    {
        using var view = new VolumeMixerView();
        var changes = new List<VolumeMixerChange>();
        view.ChangeRequested += changes.Add;
        var source = Source(.80f);
        view.Render(new("endpoint", [source, Source(.50f) with { Identity = "other", Name = "Other" }], null));
        var slider = Find<TrackBar>(view).Single(control => control.AccessibleName == "Music volume");
        slider.Value = 20;
        Find<CheckBox>(view).Single(control => control.AccessibleName == "Music mute").Checked = true;
        Assert.Equal(2, changes.Count);
        Assert.All(changes, change => Assert.Same(source, change.Source));
        Assert.Equal(.20f, changes[0].Volume);
        Assert.Null(changes[0].Muted);
        Assert.Null(changes[1].Volume);
        Assert.True(changes[1].Muted);
    });

    [Fact]
    public void Mixed_and_readonly_sources_are_not_silently_normalized_when_rendered() => OnSta(() =>
    {
        using var view = new VolumeMixerView();
        var changes = new List<VolumeMixerChange>();
        view.ChangeRequested += changes.Add;
        var mixed = Source(.4f) with { Volume = null, Muted = null, HasMixedVolumes = true, HasMixedMute = true };
        view.Render(Census(mixed));
        Assert.Contains(Find<Label>(view), label => label.Text == "Mixed");
        Assert.Equal(CheckState.Indeterminate, Find<CheckBox>(view).Single().CheckState);
        Assert.Empty(changes);
        view.Render(Census(Source(.8f) with { CanControl = false, Error = "Shared session" }));
        Assert.False(Slider(view).Enabled);
        Assert.False(Find<CheckBox>(view).Single().Enabled);
        Slider(view).Value = 12;
        Assert.Empty(changes);
    });

    [Fact]
    public void Converting_a_mixed_mute_state_to_muted_is_an_explicit_write() => OnSta(() =>
    {
        using var view = new VolumeMixerView();
        var changes = new List<VolumeMixerChange>();
        view.ChangeRequested += changes.Add;
        view.Render(Census(Source(.4f) with { Muted = null, HasMixedMute = true }));
        Find<CheckBox>(view).Single().CheckState = CheckState.Checked;
        Assert.True(Assert.Single(changes).Muted);
    });

    [Fact]
    public void Failed_write_releases_pending_thumb_and_allows_a_fresh_read_or_retry() => OnSta(() =>
    {
        using var view = new VolumeMixerView();
        VolumeMixerChange? change = null;
        view.ChangeRequested += current => change = current;
        view.Render(Census(Source(.8f)));
        Slider(view).Value = 25;
        Assert.NotNull(change);
        view.CompleteWrite(change!, new(false, 0, 1, "Session restarted", null));
        view.Render(Census(Source(.6f)));
        Assert.Equal(60, Slider(view).Value);
        Assert.Contains(Find<Label>(view), label => label.Text == "Session restarted");
    });

    private static AudioSessionVolumeSnapshot Source(float volume, bool muted = false) => new(
        "endpoint", "music", "Music", volume, muted, false, false,
        [new("endpoint", "music", 10, DateTimeOffset.UnixEpoch, @"C:\Music.exe", "session", false)],
        true, true, null);
    private static AudioSessionVolumeCensus Census(AudioSessionVolumeSnapshot source) => new("endpoint", [source], null);
    private static TrackBar Slider(Control parent) => Find<TrackBar>(parent).Single();
    private static IEnumerable<T> Find<T>(Control control) where T : Control =>
        control.Controls.Cast<Control>().SelectMany(child => (child is T match ? new[] { match } : []).Concat(Find<T>(child)));

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Mixer UI test did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using WindowsSourceSwitcher.Tray;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class VolumeMixerMeterTests
{
    private static readonly long Now = Stopwatch.Frequency * 10;

    [Fact]
    public void Meter_frames_preserve_controls_pending_slider_and_mute_without_emitting_writes() => OnSta(() =>
    {
        using var view = new VolumeMixerView();
        var source = Source();
        var changes = new List<VolumeMixerChange>();
        view.ChangeRequested += changes.Add;
        view.Render(Census(source));
        var slider = Find<TrackBar>(view).Single();
        var mute = Find<CheckBox>(view).Single();
        var meter = Meter(view);
        slider.Value = 65;
        mute.Checked = true;
        Assert.Equal(2, changes.Count);
        for (var sequence = 1; sequence <= 100; sequence++)
            view.RenderMeters(Frame(source, .1f, sequence: sequence), Now);
        Assert.Same(slider, Find<TrackBar>(view).Single());
        Assert.Same(mute, Find<CheckBox>(view).Single());
        Assert.Same(meter, Meter(view));
        Assert.Equal(65, slider.Value);
        Assert.True(mute.Checked);
        Assert.Equal(2, changes.Count);
        Assert.Equal("-20.0 dBFS", meter.LevelText);
        view.Render(Census(source with { Volume = .20f }));
        Assert.Equal(65, slider.Value);
        Assert.Equal(2, changes.Count);
    });

    [Fact]
    public void Meter_exposes_current_signal_and_unknown_separately_without_claiming_dac_loudness() => OnSta(() =>
    {
        using var view = new VolumeMixerView();
        var source = Source();
        view.Render(Census(source));
        var meter = Meter(view);
        Assert.Equal("Music audio level", meter.AccessibleName);
        Assert.True(meter.AccessibilityObject.State.HasFlag(AccessibleStates.ReadOnly));
        Assert.Equal("Unavailable", meter.AccessibilityObject.Value);
        view.RenderMeters(Frame(source, 0), Now);
        Assert.True(meter.IsAvailable);
        Assert.Equal("Quiet", meter.AccessibilityObject.Value);
        Assert.Contains("not a physical DAC", meter.AccessibilityObject.Description);
        view.RenderMeters(Frame(source, null, sequence: 2), Now);
        Assert.False(meter.IsAvailable);
        Assert.Equal("Unavailable", meter.AccessibilityObject.Value);
    });

    [Fact]
    public void Session_replacement_clears_old_peak_and_rejects_old_session_sample() => OnSta(() =>
    {
        using var view = new VolumeMixerView();
        var original = Source();
        view.Render(Census(original));
        view.RenderMeters(Frame(original, 1), Now);
        var replacement = original with { Sessions = [original.Sessions[0] with { SessionInstanceId = "restarted-session" }] };
        view.Render(Census(replacement));
        Assert.False(Meter(view).IsAvailable);
        view.RenderMeters(Frame(original, 1, sequence: 2), Now);
        Assert.False(Meter(view).IsAvailable);
        view.RenderMeters(Frame(replacement, .1f, sequence: 3), Now);
        Assert.True(Meter(view).IsAvailable);
    });

    [Fact]
    public void Old_write_ack_after_session_restart_cannot_restore_old_slider_mute_or_meter_identity() => OnSta(() =>
    {
        using var view = new VolumeMixerView();
        var source = Source();
        var changes = new List<VolumeMixerChange>();
        view.ChangeRequested += changes.Add;
        view.Render(Census(source));
        Find<TrackBar>(view).Single().Value = 30;
        Find<CheckBox>(view).Single().Checked = true;
        Assert.Equal(2, changes.Count);
        var replacement = source with { Volume = .8f, Sessions = [source.Sessions[0] with { SessionInstanceId = "new-session" }] };
        view.Render(Census(replacement));
        Assert.Equal(80, Find<TrackBar>(view).Single().Value);
        Assert.False(Find<CheckBox>(view).Single().Checked);
        view.RenderMeters(Frame(replacement, .1f), Now);
        view.CompleteWrite(changes[0], new(true, 1, 0, null, source with { Volume = .3f }));
        view.CompleteWrite(changes[1], new(true, 1, 0, null, source with { Muted = true }));
        Assert.Equal(80, Find<TrackBar>(view).Single().Value);
        Assert.False(Find<CheckBox>(view).Single().Checked);
        view.RenderMeters(Frame(source, 1, sequence: 2), Now);
        Assert.False(Meter(view).IsAvailable);
        view.RenderMeters(Frame(replacement, .1f, sequence: 3), Now);
        Assert.True(Meter(view).IsAvailable);
        Assert.Equal(2, changes.Count);
    });

    [Fact]
    public void Session_restart_during_drag_discards_the_old_gesture_until_mouse_up() => OnSta(() =>
    {
        using var view = new VolumeMixerView();
        var source = Source();
        var changes = new List<VolumeMixerChange>();
        view.ChangeRequested += changes.Add;
        view.Render(Census(source));
        var slider = Find<TrackBar>(view).Single();
        Mouse(slider, "OnMouseDown");
        slider.Value = 30;
        var replacement = source with { Volume = .8f, Sessions = [source.Sessions[0] with { SessionInstanceId = "new-session" }] };
        view.Render(Census(replacement));
        Assert.Equal(80, slider.Value);
        slider.Value = 20; // Further events from the same old drag cannot control a newly opened session.
        Mouse(slider, "OnMouseUp");
        Assert.Single(changes);
        Assert.Same(source, changes[0].Source);
        Assert.Equal(80, slider.Value);
        slider.Value = 45; // A new keyboard or pointer gesture controls the current source normally.
        Assert.Equal(2, changes.Count);
        Assert.Same(replacement, changes[1].Source);
    });

    [Fact]
    public void Same_process_with_changed_creation_time_cannot_supply_the_replacement_meter() => OnSta(() =>
    {
        using var view = new VolumeMixerView();
        var source = Source();
        view.Render(Census(source));
        var stale = source with { Sessions = [source.Sessions[0] with { ProcessStartedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(1) }] };
        view.RenderMeters(Frame(stale, 1), Now);
        Assert.False(Meter(view).IsAvailable);
    });

    [Fact]
    public void Group_binding_is_order_independent_but_requires_every_unique_session() => OnSta(() =>
    {
        using var view = new VolumeMixerView();
        var source = Source();
        var first = source.Sessions[0];
        var second = first with { SessionInstanceId = "second-session" };
        source = source with { Sessions = [first, second] };
        view.Render(Census(source));
        view.RenderMeters(Frame(source with { Sessions = [second, first] }, .1f), Now);
        Assert.True(Meter(view).IsAvailable);
        view.RenderMeters(Frame(source with { Sessions = [first] }, 1, sequence: 2), Now);
        Assert.False(Meter(view).IsAvailable);
        view.RenderMeters(Frame(source with { Sessions = [first, first] }, 1, sequence: 3), Now);
        Assert.False(Meter(view).IsAvailable);
    });

    [Fact]
    public void Wrong_endpoint_and_out_of_order_frames_cannot_overwrite_a_current_reading() => OnSta(() =>
    {
        using var view = new VolumeMixerView();
        var source = Source();
        view.Render(Census(source));
        view.RenderMeters(Frame(source, .1f, generation: 2, sequence: 3), Now);
        view.RenderMeters(Frame(source, 1, generation: 1, sequence: 99), Now);
        view.RenderMeters(Frame(source, 1, generation: 2, sequence: 2), Now);
        view.RenderMeters(Frame(source, 1, generation: 2, sequence: 4) with { EndpointId = "other" }, Now);
        Assert.Equal("-20.0 dBFS", Meter(view).LevelText);
        view.Render(Census(source with { EndpointId = "new-output" }) with { EndpointId = "new-output" });
        Assert.False(Meter(view).IsAvailable);
        view.RenderMeters(Frame(source, 1, generation: 3), Now);
        Assert.False(Meter(view).IsAvailable);
    });

    [Fact]
    public void Stale_future_error_missing_and_duplicate_observations_clear_instead_of_freezing_a_peak() => OnSta(() =>
    {
        using var view = new VolumeMixerView();
        var source = Source();
        view.Render(Census(source));
        view.RenderMeters(Frame(source, 1), Now);
        view.ExpireMeters(Now + Stopwatch.Frequency);
        Assert.False(Meter(view).IsAvailable);
        view.RenderMeters(Frame(source, 1, sequence: 2), Now + Stopwatch.Frequency);
        Assert.False(Meter(view).IsAvailable);
        view.RenderMeters(Frame(source, 1, sequence: 3) with { SampledAtTimestamp = Now + Stopwatch.Frequency }, Now);
        Assert.False(Meter(view).IsAvailable);
        view.RenderMeters(Frame(source, .1f, sequence: 4), Now);
        Assert.True(Meter(view).IsAvailable); // A malformed future timestamp cannot poison later current samples.
        view.RenderMeters(Frame(source, 1, generation: 2) with { Error = "Device disconnected" }, Now);
        Assert.False(Meter(view).IsAvailable);
        view.RenderMeters(Frame(source, 1, generation: 3) with { Sources = [] }, Now);
        Assert.False(Meter(view).IsAvailable);
        var duplicate = new AudioSourcePeak(source.Identity, source.Sessions, 1, null);
        view.RenderMeters(Frame(source, 1, generation: 4) with { Sources = [duplicate, duplicate] }, Now);
        Assert.False(Meter(view).IsAvailable);
    });

    [Fact]
    public void Stop_clears_immediately_and_a_readonly_source_can_still_show_a_valid_meter() => OnSta(() =>
    {
        using var view = new VolumeMixerView();
        var source = Source() with { CanControl = false, Error = "Shared session" };
        view.Render(Census(source));
        view.RenderMeters(Frame(source, 1), Now);
        Assert.True(Meter(view).IsAvailable);
        Assert.False(Find<TrackBar>(view).Single().Enabled);
        view.StopMeters();
        Assert.False(Meter(view).IsAvailable);
        Assert.Equal(0, Meter(view).DisplayFraction);
    });

    private static AudioSessionVolumeSnapshot Source() => new("endpoint", "music", "Music", .5f, false, false, false,
        [new("endpoint", "music", 10, DateTimeOffset.UnixEpoch, @"C:\Music.exe", "session", false)], true, true, null);
    private static AudioSessionVolumeCensus Census(AudioSessionVolumeSnapshot source) => new("endpoint", [source], null);
    private static AudioMeterFrame Frame(AudioSessionVolumeSnapshot source, float? peak, long generation = 1, long sequence = 1) =>
        new("endpoint", generation, sequence, Now, [new(source.Identity, source.Sessions, peak, null)], null);
    private static AudioPeakMeterView Meter(Control view) => Find<AudioPeakMeterView>(view).Single();
    private static void Mouse(Control control, string method) => typeof(Control)
        .GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(control, [new MouseEventArgs(MouseButtons.Left, 1, 10, 10, 0)]);
    private static IEnumerable<T> Find<T>(Control control) where T : Control =>
        control.Controls.Cast<Control>().SelectMany(child => (child is T match ? new[] { match } : []).Concat(Find<T>(child)));
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Meter UI test did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using WindowsLosslessSwitcher.Models;
using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class ControlsSettingsViewTests
{
    [Fact]
    public void Fullscreen_preference_is_opt_in_and_render_does_not_emit_changes() => OnSta(() =>
    {
        using var view = new ControlsSettingsView();
        var changes = new List<bool>();
        view.FullscreenAutoSwitchChanged += changes.Add;
        var choice = Find<CheckBox>(view).Single(control => control.AccessibleName == "Fullscreen app priority");
        Assert.False(choice.Checked);
        view.Render(false, false, false, true);
        Assert.True(choice.Checked);
        view.Render(false);
        Assert.Empty(changes);
        choice.Checked = true;
        choice.Checked = false;
        Assert.Equal([true, false], changes);
        Assert.Contains(Find<Label>(view), label => label.Text == "Ctrl + Alt + Shift + F");
    });

    [Fact]
    public void Apple_catalog_preference_can_be_restored_without_a_live_Apple_session() => OnSta(() =>
    {
        using var popup = new SourcePopup();
        var requests = new List<bool>();
        popup.CatalogChanged += requests.Add;
        popup.Render([], [], [], null, new SourceSettings(), "Status", "Evidence", false);
        var choice = Find<CheckBox>(popup).Single(control => control.Text == "Use Apple Music catalog rate (estimate)");
        Assert.True(choice.Enabled);
        choice.Checked = true;
        choice.Checked = false;
        Assert.Equal([true, false], requests);
    });

    [Fact]
    public void Favorite_auto_switch_is_opt_in_and_only_explicit_changes_raise_requests() => OnSta(() =>
    {
        using var view = new ControlsSettingsView();
        var changes = new List<bool>();
        view.FavoriteAutoSwitchChanged += changes.Add;
        var choice = Find<CheckBox>(view).Single(control => control.AccessibleName == "Auto-switch by source priority");
        Assert.False(choice.Checked);
        view.Render(false, false, true);
        view.Render(false, false, false);
        Assert.Empty(changes);
        choice.Checked = true;
        choice.Checked = false;
        Assert.Equal([true, false], changes);
    });

    [Fact]
    public void Hotkeys_start_opted_out_and_rendering_saved_preferences_emits_no_requests() => OnSta(() =>
    {
        using var view = new ControlsSettingsView();
        var toggles = new List<bool>();
        var retries = 0;
        view.GlobalHotkeysChanged += toggles.Add;
        view.HotkeyRetryRequested += () => retries++;
        Assert.False(Hotkeys(view).Checked);
        Assert.False(Retry(view).Enabled);

        view.Render(true);
        view.Render(false);
        view.Render(true);
        _ = view.Handle;

        Assert.True(Hotkeys(view).Checked);
        Assert.True(Retry(view).Enabled);
        Assert.Empty(toggles);
        Assert.Equal(0, retries);
    });

    [Fact]
    public void Explicit_checkbox_changes_request_enable_and_disable_once_each() => OnSta(() =>
    {
        using var view = new ControlsSettingsView();
        var toggles = new List<bool>();
        view.GlobalHotkeysChanged += toggles.Add;
        var checkbox = Hotkeys(view);
        Assert.Equal("Enable global hotkeys", checkbox.AccessibleName);

        checkbox.Checked = true;
        checkbox.Checked = true;
        Assert.True(Retry(view).Enabled);
        checkbox.Checked = false;

        Assert.Equal([true, false], toggles);
        Assert.False(Retry(view).Enabled);
    });

    [Fact]
    public void Retry_is_an_explicit_action_only_when_hotkeys_are_enabled() => OnSta(() =>
    {
        using var view = new ControlsSettingsView();
        var requests = 0;
        view.HotkeyRetryRequested += () => requests++;
        var retry = Retry(view);
        Click(retry);
        Assert.Equal(0, requests);
        view.Render(true);
        Click(retry);
        Assert.Equal(1, requests);
        view.Render(false);
        Click(retry);
        Assert.Equal(1, requests);
    });

    [Fact]
    public void Host_status_updates_preserve_choice_and_do_not_retry_registration() => OnSta(() =>
    {
        using var view = new ControlsSettingsView();
        var events = 0;
        view.GlobalHotkeysChanged += _ => events++;
        view.HotkeyRetryRequested += () => events++;
        view.Render(true);

        view.SetHotkeyStatus("Next favorite unavailable: shortcut already registered by another app.");
        view.SetHotkeyStatus("All five global hotkeys are registered.");

        var status = Find<Label>(view).Single(label => label.AccessibleName == "Global hotkey status");
        Assert.Equal("All five global hotkeys are registered.", status.Text);
        Assert.True(Hotkeys(view).Checked);
        Assert.Equal(0, events);
    });

    [Fact]
    public void Instructions_explain_active_favorite_scope_and_all_fixed_chords() => OnSta(() =>
    {
        using var view = new ControlsSettingsView();
        var labels = Find<Label>(view).Select(label => label.Text).ToArray();
        foreach (var key in new[] { "O", "M", "Left", "Right", "R" })
            Assert.Contains("Ctrl + Alt + Shift + " + key, labels);
        Assert.Contains("Previous active favorite", labels);
        Assert.Contains("Next active favorite", labels);
        Assert.Contains(labels, label => label.Contains("Favorites without an active Windows audio session are skipped", StringComparison.Ordinal));
        Assert.Contains(labels, label => label.Contains("Some paused apps keep their session active", StringComparison.Ordinal));
        Assert.Contains(labels, label => label.Contains("window is hidden", StringComparison.Ordinal));
        Assert.DoesNotContain(labels, label => label.Contains("profile", StringComparison.OrdinalIgnoreCase));
    });

    [Fact]
    public void Popup_routes_hotkey_preferences_and_retry_without_selecting_source_or_rate() => OnSta(() =>
    {
        using var popup = new SourcePopup();
        var changes = new List<bool>();
        var retries = 0;
        var audioChanges = 0;
        popup.GlobalHotkeysChanged += changes.Add;
        popup.HotkeyRetryRequested += () => retries++;
        popup.SourceSelected += _ => audioChanges++;
        popup.SavedSourceSelected += (_, _) => audioChanges++;
        popup.RateSelected += _ => audioChanges++;
        popup.VolumeChangeRequested += _ => audioChanges++;
        var settings = new SourceSettings { GlobalHotkeysEnabled = true };

        popup.Render([], [], [], null, settings, "Status", "Evidence", false);
        popup.SelectControlsTab();
        popup.SetHotkeyStatus("Registered.");
        Assert.Empty(changes);
        var controls = Find<ControlsSettingsView>(popup).Single();
        Click(Retry(controls));
        Hotkeys(controls).Checked = false;

        Assert.Equal([false], changes);
        Assert.Equal(1, retries);
        Assert.Equal(0, audioChanges);
        Assert.True(settings.GlobalHotkeysEnabled); // The host owns persistence.
        Assert.Equal("Registered.", Find<Label>(controls).Single(label => label.AccessibleName == "Global hotkey status").Text);
    });

    [Fact]
    public void Explicit_tab_navigation_preserves_settings_and_mixer_visibility_contract() => OnSta(() =>
    {
        using var popup = new SourcePopup();
        var tabs = Find<TabControl>(popup).Single();
        Assert.Equal(["Sources", "Mixer", "Settings", "Diagnostics"], tabs.TabPages.Cast<TabPage>().Select(page => page.Text));
        popup.SelectControlsTab();
        Assert.Equal("Settings", tabs.SelectedTab!.Text);
        Assert.False(popup.MixerVisible);
        popup.SelectMixerTab();
        Assert.Equal("Mixer", tabs.SelectedTab!.Text);
        Assert.False(popup.MixerVisible); // Selecting a tab does not show the hidden popup or start metering.
        popup.SelectSourcesTab();
        Assert.Equal("Sources", tabs.SelectedTab!.Text);
        Assert.False(popup.Visible);
    });

    [Fact]
    public void Available_update_marks_only_settings_and_stays_visible_during_download() => OnSta(() =>
    {
        using var popup = new SourcePopup();
        var buttons = Find<SidebarButton>(popup).ToArray();
        var settings = buttons.Single(button => button.Text == "Settings");
        Assert.False(settings.UpdateAvailable);
        popup.SetUpdateStatus("New version", true, true, true);
        Assert.Same(settings, Assert.Single(buttons, button => button.UpdateAvailable));
        popup.SetUpdateStatus("Downloading", false, false, true);
        Assert.True(settings.UpdateAvailable);
        popup.SetUpdateStatus("Up to date", true, false, false);
        Assert.False(settings.UpdateAvailable);
    });

    [Fact]
    public void Update_changelog_has_distinct_close_and_install_actions() => OnSta(() =>
    {
        using var dialog = new UpdateChangelogDialog(new(new Version(1, 2, 1), 1, 10, "digest", "- New update preview."));
        Assert.Contains("v1.2.1", dialog.Text);
        var notes = Assert.Single(Find<TextBox>(dialog));
        Assert.True(notes.ReadOnly);
        Assert.Equal("- New update preview.", notes.Text);
        var buttons = Find<Button>(dialog).ToArray();
        Assert.Equal(DialogResult.Cancel, Assert.Single(buttons, button => button.Text == "Close").DialogResult);
        Assert.Equal(DialogResult.OK, Assert.Single(buttons, button => button.Text == "Update").DialogResult);
    });

    [Fact]
    public void Rate_dropdown_omits_formats_unsupported_at_the_current_depth_and_channel_count() => OnSta(() =>
    {
        using var popup = new SourcePopup();
        var settings = new SourceSettings();
        const string key = @"C:\Players\player.exe";
        settings.SelectMaster(key, "Player");
        settings.ManualSampleRatesHz[key] = 768000;
        var requests = new List<int?>();
        popup.RateSelected += requests.Add;
        AudioFormatCandidate[] formats = [new(48000, 24, 2), new(96000, 24, 2), new(192000, 16, 2), new(768000, 24, 6)];
        popup.Render([], [], formats, formats[0], settings, "Status", "Evidence", false);
        var rate = Find<ComboBox>(popup).Single(combo => combo.AccessibleName == "Selected app's target rate");
        Assert.Equal(3, rate.Items.Count);
        Assert.DoesNotContain(rate.Items.Cast<object>(), item => item.ToString()!.Contains("192 kHz") || item.ToString()!.Contains("768 kHz"));
        Assert.Null(rate.SelectedItem);
        Assert.Equal(768000, settings.ManualSampleRatesHz[key]);
        Assert.Contains(Find<Label>(popup), label => label.Text.Contains("Saved override 768 kHz is unavailable"));
        Assert.Empty(requests);
        rate.SelectedIndex = 0;
        Assert.Equal([null], requests);
        popup.Render([], [], formats, formats[2], settings, "Status", "Evidence", false);
        Assert.Contains(rate.Items.Cast<object>(), item => item.ToString()!.Contains("192 kHz"));
    });

    [Fact]
    public void Rate_detection_card_stays_stable_when_an_automatic_status_wraps() => OnSta(() =>
    {
        using var settings = new ControlsSettingsView { Size = new System.Drawing.Size(550, 490) };
        settings.CreateControl();
        settings.SetFavoriteAutoSwitchStatus("Automatic switching is off.");
        settings.PerformLayout();
        var heading = Find<Label>(settings).Single(label => label.Text == "Rate detection");
        var card = Assert.IsType<SectionCard>(heading.Parent!.Parent);
        var originalTop = card.Top;
        settings.SetFavoriteAutoSwitchStatus("Checking playback." + Environment.NewLine + "Waiting for the selected app.");
        settings.PerformLayout();
        // WinForms may round the scaled table row by one pixel.
        Assert.InRange(card.Top, originalTop - 1, originalTop + 1);
    });

    private static CheckBox Hotkeys(Control view) => Find<CheckBox>(view).Single(control => control.AccessibleName == "Enable global hotkeys");
    private static Button Retry(Control view) => Find<Button>(view).Single(control => control.AccessibleName == "Retry global hotkey registration");
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
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Controls UI test did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

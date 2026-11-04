using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class MinimalDarkThemeTests
{
    [Fact]
    public void Applying_and_reapplying_theme_preserves_control_values_and_emits_no_user_actions() => OnSta(() =>
    {
        using var form = new Form();
        var checkbox = new MinimalCheckBox { Text = "Auto-detect", Checked = true, AccessibleName = "Detection preference" };
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(["Automatic", "96 kHz"]);
        combo.SelectedIndex = 1;
        var volume = new TrackBar { Minimum = 0, Maximum = 100, Value = 37 };
        var button = new Button { Text = "Refresh", AccessibleName = "Refresh without resetting selection" };
        form.Controls.AddRange([checkbox, combo, volume, button]);
        var changes = 0;
        checkbox.CheckedChanged += (_, _) => changes++;
        combo.SelectedIndexChanged += (_, _) => changes++;
        volume.ValueChanged += (_, _) => changes++;
        button.Click += (_, _) => changes++;

        MinimalDarkTheme.Apply(form);
        MinimalDarkTheme.Apply(form);
        _ = form.Handle;

        Assert.True(checkbox.Checked);
        Assert.Equal(1, combo.SelectedIndex);
        Assert.Equal(37, volume.Value);
        Assert.Equal("Detection preference", checkbox.AccessibleName);
        Assert.Equal("Refresh without resetting selection", button.AccessibleName);
        Assert.Equal(0, changes);
        Assert.Equal(FlatStyle.Flat, button.FlatStyle);
        Assert.Equal(DrawMode.OwnerDrawFixed, combo.DrawMode);
    });

    [Fact]
    public void Dynamically_created_mixer_style_children_receive_palette_without_a_timer_or_rebuild() => OnSta(() =>
    {
        using var parent = new Panel();
        MinimalDarkTheme.Apply(parent);
        var row = new Panel();
        var label = new Label { Text = "Spotify", ForeColor = System.Drawing.Color.DimGray };
        var mute = new MinimalCheckBox { Text = "Mute", Checked = true };
        row.Controls.AddRange([label, mute]);

        parent.Controls.Add(row);

        Assert.Equal(MinimalDarkTheme.Background, row.BackColor);
        Assert.Equal(MinimalDarkTheme.MutedText, label.ForeColor);
        Assert.Equal(MinimalDarkTheme.Text, mute.ForeColor);
        Assert.True(mute.Checked);
    });

    [Fact]
    public void Painted_checkbox_retains_native_accessibility_and_one_click_toggle_semantics() => OnSta(() =>
    {
        using var checkbox = new MinimalCheckBox { Text = "Mute", AccessibleName = "Spotify mute" };
        MinimalDarkTheme.Apply(checkbox);
        _ = checkbox.Handle;
        var changes = 0;
        checkbox.CheckedChanged += (_, _) => changes++;
        var click = typeof(CheckBox).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!;

        click.Invoke(checkbox, [EventArgs.Empty]);

        Assert.True(checkbox.Checked);
        Assert.Equal(1, changes);
        Assert.Equal(AccessibleRole.CheckButton, checkbox.AccessibilityObject.Role);
        Assert.Equal("Spotify mute", checkbox.AccessibilityObject.Name);
        Assert.True(checkbox.AccessibilityObject.State.HasFlag(AccessibleStates.Checked));
        click.Invoke(checkbox, [EventArgs.Empty]);
        Assert.False(checkbox.Checked);
        Assert.Equal(2, changes);
    });

    [Fact]
    public void Painted_tabs_keep_base_selection_events_pages_and_no_show_side_effect() => OnSta(() =>
    {
        using var form = new Form();
        var tabs = new MinimalTabControl { Dock = DockStyle.Fill };
        var pages = new[] { new TabPage("Sources"), new TabPage("Mixer"), new TabPage("Controls"), new TabPage("Diagnostics") };
        tabs.TabPages.AddRange(pages);
        form.Controls.Add(tabs);
        MinimalDarkTheme.Apply(form);
        _ = form.Handle;
        _ = tabs.Handle;
        var selections = 0;
        tabs.SelectedIndexChanged += (_, _) => selections++;

        tabs.SelectedTab = pages[1];
        tabs.SelectedTab = pages[1];
        tabs.SelectedTab = pages[3];

        Assert.Equal(2, selections);
        Assert.Same(pages[3], tabs.SelectedTab);
        Assert.Equal(["Sources", "Mixer", "Controls", "Diagnostics"], tabs.TabPages.Cast<TabPage>().Select(page => page.Text));
        Assert.False(form.Visible);
        Assert.Equal(AccessibleRole.PageTabList, tabs.AccessibilityObject.Role);
    });

    [Fact]
    public void Text_and_muted_text_remain_readable_against_dark_background_and_selection()
    {
        if (SystemInformation.HighContrast)
        {
            Assert.Equal(System.Drawing.SystemColors.ControlText, MinimalDarkTheme.Text);
            Assert.Equal(System.Drawing.SystemColors.HighlightText, MinimalDarkTheme.SelectedText);
            return;
        }
        Assert.True(Contrast(MinimalDarkTheme.Text, MinimalDarkTheme.Background) >= 7);
        Assert.True(Contrast(MinimalDarkTheme.MutedText, MinimalDarkTheme.Surface) >= 4.5);
        Assert.True(Contrast(MinimalDarkTheme.SelectedText, MinimalDarkTheme.SelectedSurface) >= 4.5);
    }

    private static double Contrast(System.Drawing.Color first, System.Drawing.Color second)
    {
        static double Channel(byte value)
        {
            var channel = value / 255d;
            return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
        }
        static double Luminance(System.Drawing.Color color) => .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        var firstLight = Luminance(first);
        var secondLight = Luminance(second);
        return (Math.Max(firstLight, secondLight) + .05) / (Math.Min(firstLight, secondLight) + .05);
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Theme UI test did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

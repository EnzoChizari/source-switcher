using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class MinimalComboBoxTests
{
    [Fact]
    public void Rendering_dark_native_combo_preserves_selection_accessibility_and_handle() => OnSta(() =>
    {
        using var form = new Form();
        using var combo = new MinimalComboBox { Width = 240, AccessibleName = "Selected source target rate" };
        combo.Items.AddRange(["Automatic", "96 kHz"]);
        combo.SelectedIndex = 1;
        form.Controls.Add(combo);
        _ = form.Handle;
        var handle = combo.Handle;
        var changes = 0;
        combo.SelectedIndexChanged += (_, _) => changes++;

        using var bitmap = new Bitmap(combo.Width, combo.Height);
        combo.DrawToBitmap(bitmap, combo.ClientRectangle);
        combo.DrawToBitmap(bitmap, combo.ClientRectangle);

        Assert.Equal(handle, combo.Handle);
        Assert.Equal(1, combo.SelectedIndex);
        Assert.Equal(0, changes);
        Assert.Equal(AccessibleRole.ComboBox, combo.AccessibilityObject.Role);
        Assert.Equal("Selected source target rate", combo.AccessibilityObject.Name);
        Assert.Equal("96 kHz", combo.AccessibilityObject.Value);
        if (!SystemInformation.HighContrast)
        {
            var arrowBackground = bitmap.GetPixel(combo.Width - 5, combo.Height / 2);
            Assert.Equal(MinimalDarkTheme.Surface.ToArgb(), arrowBackground.ToArgb());
        }
    });

    [Fact]
    public void Native_arrow_key_selection_still_emits_the_normal_single_change_event() => OnSta(() =>
    {
        using var form = new Form();
        using var combo = new MinimalComboBox();
        combo.Items.AddRange(["Automatic", "48 kHz", "96 kHz"]);
        combo.SelectedIndex = 0;
        form.Controls.Add(combo);
        _ = form.Handle;
        _ = combo.Handle;
        var changes = 0;
        combo.SelectedIndexChanged += (_, _) => changes++;

        SendMessage(combo.Handle, 0x0100 /* WM_KEYDOWN */, new IntPtr(0x28 /* VK_DOWN */), IntPtr.Zero);
        SendMessage(combo.Handle, 0x0101 /* WM_KEYUP */, new IntPtr(0x28), IntPtr.Zero);

        Assert.Equal(1, combo.SelectedIndex);
        Assert.Equal(1, changes);
        Assert.Equal("48 kHz", combo.AccessibilityObject.Value);
    });

    [Fact]
    public void Disabled_checkbox_tick_remains_distinct_from_its_fill() => OnSta(() =>
    {
        if (SystemInformation.HighContrast) return;
        using var checkbox = new MinimalCheckBox { Text = "Muted", Checked = true, Enabled = false, AutoSize = false, Size = new Size(140, 26) };
        MinimalDarkTheme.Apply(checkbox);
        _ = checkbox.Handle;
        using var bitmap = new Bitmap(checkbox.Width, checkbox.Height);
        checkbox.DrawToBitmap(bitmap, checkbox.ClientRectangle);
        var scale = checkbox.DeviceDpi / 96d;
        var left = (int)Math.Round(scale);
        var side = (int)Math.Round(15 * scale);
        var top = (checkbox.Height - side) / 2;
        var inset = Math.Max(2, (int)Math.Round(3 * scale));
        var colors = new List<Color>();
        for (var y = top + inset; y < top + side - inset; y++)
            for (var x = left + inset; x < left + side - inset; x++) colors.Add(bitmap.GetPixel(x, y));
        static double Light(Color color)
        {
            static double Channel(byte value)
            {
                var channel = value / 255d;
                return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
            }
            return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        }
        var contrast = (colors.Max(Light) + .05) / (colors.Min(Light) + .05);
        Assert.True(contrast >= 3, $"Disabled checkmark contrast was {contrast:0.00}.");
        Assert.True(checkbox.Checked);
        Assert.False(checkbox.Enabled);
    });

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Native combo appearance test did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

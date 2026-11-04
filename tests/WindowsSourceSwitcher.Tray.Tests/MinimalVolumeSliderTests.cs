using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Xunit;
using Xunit.Abstractions;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class MinimalVolumeSliderTests(ITestOutputHelper output)
{
    [Fact]
    public void NativeSliderRetainsRangeKeyboardAndAccessibilityAndPaintsWithoutValueChanges() => OnSta(() =>
    {
        using var form = new Form { ClientSize = new Size(440, 100), Opacity = 0 };
        using var slider = new MinimalVolumeSlider { Minimum = 0, Maximum = 100, Value = 50,
            SmallChange = 1, LargeChange = 5, Bounds = new Rectangle(10, 15, 400, 30), BackColor = MinimalDarkTheme.Surface };
        form.Controls.Add(slider); form.Show(); Application.DoEvents();
        var bounds = new NativeRectangle();
        SendRectangle(slider.Handle, 0x419, 0, ref bounds);
        output.WriteLine($"NATIVE_THUMB left={bounds.Left};top={bounds.Top};right={bounds.Right};bottom={bounds.Bottom}");
        var changes = 0;
        slider.ValueChanged += (_, _) => changes++;
        using var image = new Bitmap(slider.Width, slider.Height);
        slider.DrawToBitmap(image, slider.ClientRectangle);
        Assert.Equal(0, changes);
        Assert.Equal(50, slider.Value);
        Assert.Equal(AccessibleRole.Slider, slider.AccessibilityObject.Role);
        if (!SystemInformation.HighContrast)
        {
            var center = (bounds.Left + bounds.Right) / 2;
            var orangePixels = Enumerable.Range(0, image.Height).Count(y => image.GetPixel(center, y).ToArgb() == Color.FromArgb(255, 152, 0).ToArgb());
            Assert.True(orangePixels >= 18, $"Round thumb is clipped or absent: {orangePixels} vertical orange pixels.");
        }
        slider.Focus();
        SendMessage(slider.Handle, 0x100, (nint)Keys.Right, 0);
        SendMessage(slider.Handle, 0x101, (nint)Keys.Right, 0);
        Assert.Equal(51, slider.Value);
        SendMessage(slider.Handle, 0x100, (nint)Keys.End, 0);
        SendMessage(slider.Handle, 0x101, (nint)Keys.End, 0);
        Assert.Equal(100, slider.Value);
        SendMessage(slider.Handle, 0x100, (nint)Keys.Home, 0);
        SendMessage(slider.Handle, 0x101, (nint)Keys.Home, 0);
        Assert.Equal(0, slider.Value);
    });
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Slider test did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendRectangle(nint window, uint message, nint wParam, ref NativeRectangle rectangle);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}

using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class DarkScrollBarPainterTests
{
    [Fact]
    public void NativeVerticalAndHorizontalBarsPaintDarkAndStillScroll() => OnSta(() =>
    {
        using var form = new Form { ClientSize = new Size(240, 210), ShowInTaskbar = false };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
        list.Columns.Add("Source", 360);
        list.Columns.Add("Rate", 140);
        for (var index = 0; index < 100; index++) list.Items.Add(new ListViewItem(["Source " + index, "192 kHz"]));
        form.Controls.Add(list);
        MinimalDarkTheme.Apply(form);
        form.Show();
        Application.DoEvents();
        AssertDarkBars(list, -5, -6);
        var initial = list.TopItem!.Index;
        SendMessage(list.Handle, 0x0115, (IntPtr)3 /* SB_PAGEDOWN */, IntPtr.Zero);
        Application.DoEvents();
        Assert.True(list.TopItem!.Index > initial);
        list.Columns.Clear(); // Disposing/recreating native chrome must remain safe.
        Application.DoEvents();
    });

    [Fact]
    public void SettingsAndChangelogScrollbarsStayDarkWhenAModalDisablesItsOwner() => OnSta(() =>
    {
        using var owner = new Form { ClientSize = new Size(510, 340), ShowInTaskbar = false };
        var settings = new ControlsSettingsView();
        owner.Controls.Add(settings);
        MinimalDarkTheme.Apply(owner);
        owner.Show();
        Application.DoEvents();
        AssertDarkBars(settings, -5);

        using var dialog = new UpdateChangelogDialog(new(new Version(1, 2, 4), 1, 10, "test",
            string.Join(Environment.NewLine, Enumerable.Range(1, 40).Select(number => $"Change {number}"))));
        Exception? failure = null;
        dialog.Shown += (_, _) => dialog.BeginInvoke(() =>
        {
            try
            {
                var notes = dialog.Controls[0].Controls.OfType<Panel>().SelectMany(panel => panel.Controls.OfType<RichTextBox>()).Single();
                AssertDarkBars(notes, -5);
                AssertDarkBars(settings, -5);
            }
            catch (Exception error) { failure = error; }
            finally { dialog.Close(); }
        });
        dialog.ShowDialog(owner);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        AssertDarkBars(settings, -5);
    });

    private static void AssertDarkBars(Control control, params int[] bars)
    {
        using var bitmap = new Bitmap(control.Width, control.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            try { Assert.True(PrintWindow(control.Handle, dc, 2)); }
            finally { graphics.ReleaseHdc(dc); }
        }
        Assert.True(GetWindowRect(control.Handle, out var window));
        foreach (var id in bars)
        {
            var info = new ScrollInfo { Size = Marshal.SizeOf<ScrollInfo>() };
            Assert.True(GetScrollBarInfo(control.Handle, id, ref info));
            Assert.Equal(0u, info.State & 0x18000u);
            var area = Rectangle.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                Rectangle.FromLTRB(info.Bounds.Left - window.Left, info.Bounds.Top - window.Top,
                    info.Bounds.Right - window.Left, info.Bounds.Bottom - window.Top));
            Assert.True(area.Width > 0 && area.Height > 0);
            var white = 0;
            for (var y = area.Top; y < area.Bottom; y++)
                for (var x = area.Left; x < area.Right; x++)
                {
                    var color = bitmap.GetPixel(x, y);
                    if (color.R > 220 && color.G > 220 && color.B > 220) white++;
                }
            Assert.True(white < area.Width * area.Height / 20,
                $"{control.GetType().Name} bar {id} retained {white} white pixels.");
        }
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Native scrollbar test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct ScrollInfo
    {
        public int Size; public Rect Bounds; public int Button, ThumbTop, ThumbBottom, Reserved;
        public uint State, Arrow1, Page1, Thumb, Page2, Arrow2;
    }
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")] private static extern bool GetScrollBarInfo(IntPtr window, int id, ref ScrollInfo info);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr value, IntPtr data);
}

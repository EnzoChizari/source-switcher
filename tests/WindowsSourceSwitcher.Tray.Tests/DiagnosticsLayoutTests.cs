using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Xunit;
using Xunit.Abstractions;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class DiagnosticsLayoutTests(ITestOutputHelper output)
{
    [Fact]
    public void HeightOnlyResizingPreservesColumnWidthsWithoutNativeWidthWrites() => OnSta(() =>
    {
        using var form = new Form { ClientSize = new Size(540, 640), Opacity = 0 };
        using var view = new DiagnosticsView(_ => { });
        form.Controls.Add(view);
        MinimalDarkTheme.Apply(form);
        view.Render(new(Enumerable.Range(0, 100).Select(index => new DiagnosticField($"Field {index}", "Observed PCM")).ToArray(),
            Enumerable.Range(0, 100).Select(index => new ControlHistoryEntry(index, DateTimeOffset.UnixEpoch, "Test event", "Read-only resize fixture")).ToArray()));
        form.Show(); Application.DoEvents();
        // Let native scrollbar/theme layout settle before measuring steady height-only changes.
        for (var warmup = 0; warmup < 6; warmup++)
        {
            form.ClientSize = new Size(540, 640 + warmup % 2);
            Application.DoEvents();
        }
        var lists = Find<ListView>(view).ToArray();
        var widths = lists.Select(list => list.Columns.Cast<ColumnHeader>().Select(column => column.Width).ToArray()).ToArray();
        using var first = new ColumnWidthSpy(lists[0]);
        using var second = new ColumnWidthSpy(lists[1]);
        for (var index = 0; index < 20; index++)
        {
            form.ClientSize = new Size(540, 640 + index % 2);
            Application.DoEvents();
            for (var list = 0; list < lists.Length; list++)
                Assert.Equal(widths[list], lists[list].Columns.Cast<ColumnHeader>().Select(column => column.Width).ToArray());
        }
        output.WriteLine($"NATIVE_COLUMN_WRITES heightChanges=20; writes={first.Writes + second.Writes}");
        Assert.Equal(0, first.Writes + second.Writes);
    });

    [Fact]
    public void PopulatedAndEmptyDiagnosticsSettleWithoutHorizontalScrollbarOrCopySideEffects() => OnSta(() =>
    {
        var copies = 0;
        using var form = new Form { ClientSize = new Size(540, 640), Opacity = 0 };
        using var view = new DiagnosticsView(_ => copies++);
        form.Controls.Add(view); MinimalDarkTheme.Apply(form); form.Show(); Application.DoEvents();
        foreach (var width in new[] { 540, 400, 680, 540 })
        foreach (var rows in new[] { 0, 1, 100, 0, 100 })
        {
            form.ClientSize = new Size(width, 640);
            view.Render(new(Enumerable.Range(0, rows).Select(index => new DiagnosticField($"Field {index}", "48 kHz")).ToArray(),
                Enumerable.Range(0, rows).Select(index => new ControlHistoryEntry(index, DateTimeOffset.UnixEpoch, "Event", "Example")).ToArray()));
            Application.DoEvents();
            foreach (var list in Find<ListView>(view))
            {
                GetClientRect(list.Handle, out var client);
                var scrollbar = new ScrollBarInfo { Size = (uint)Marshal.SizeOf<ScrollBarInfo>(), States = new uint[6] };
                var present = GetScrollBarInfo(list.Handle, -6, ref scrollbar); // OBJID_HSCROLL
                var visible = present && (scrollbar.States[0] & 0x8000) == 0; // STATE_SYSTEM_INVISIBLE
                if (visible && Environment.GetEnvironmentVariable("DX5_QA_CAPTURE_DIRECTORY") is { } directory)
                {
                    using var bitmap = new Bitmap(view.Width, view.Height);
                    view.DrawToBitmap(bitmap, view.ClientRectangle);
                    bitmap.Save(Path.Combine(directory, "diagnostics-overflow.png"));
                }
                Assert.False(visible,
                    $"Unexpected horizontal scrollbar: {list.AccessibleName}, formWidth={width}, rows={rows}, client={list.ClientSize}, nativeWidth={client.Right - client.Left}, columns={string.Join(',', list.Columns.Cast<ColumnHeader>().Select(column => column.Width))}, scrollbarState={scrollbar.States[0]}, scrollbarWidth={scrollbar.Rectangle.Right - scrollbar.Rectangle.Left}");
            }
        }
        Assert.Equal(0, copies);
    });

    private sealed class ColumnWidthSpy : NativeWindow, IDisposable
    {
        public int Writes { get; private set; }
        public ColumnWidthSpy(ListView list) => AssignHandle(list.Handle);
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x101E) Writes++; // LVM_SETCOLUMNWIDTH
            base.WndProc(ref message);
        }
        public void Dispose() => ReleaseHandle();
    }

    [Fact]
    public void EmptyHeaderSpaceUsesDarkPaletteBesideVisibleScrollbars() => OnSta(() =>
    {
        if (SystemInformation.HighContrast) return;
        using var form = new Form { ClientSize = new Size(540, 640), Opacity = 0 };
        using var view = new DiagnosticsView(_ => { });
        form.Controls.Add(view);
        MinimalDarkTheme.Apply(form);
        view.Render(new(Enumerable.Range(0, 100).Select(index => new DiagnosticField($"Field {index}", "48 kHz")).ToArray(),
            Enumerable.Range(0, 100).Select(index => new ControlHistoryEntry(index, DateTimeOffset.UnixEpoch, "Event", "Example")).ToArray()));
        form.Show();
        Application.DoEvents();

        foreach (var list in Find<ListView>(view))
        {
            var header = SendMessage(list.Handle, 0x101F /* LVM_GETHEADER */, 0, 0);
            Assert.True(GetClientRect(header, out var client));
            var columnRight = list.Columns.Cast<ColumnHeader>().Sum(column => column.Width);
            Assert.True(columnRight < client.Right, "The fixture must leave a header gap.");
            using var bitmap = new Bitmap(client.Right, client.Bottom);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.White);
            var dc = graphics.GetHdc();
            try { SendMessage(header, 0x0318 /* WM_PRINTCLIENT */, dc, 4 /* PRF_CLIENT */); }
            finally { graphics.ReleaseHdc(dc); }
            Assert.Equal(MinimalDarkTheme.Background.ToArgb(), bitmap.GetPixel(client.Right - 1, client.Bottom / 2).ToArgb());
        }
    });
    private static IEnumerable<T> Find<T>(Control parent) where T : Control => parent.Controls.Cast<Control>()
        .SelectMany(child => (child is T match ? new[] { match } : []).Concat(Find<T>(child)));
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20))) throw new TimeoutException("Diagnostics layout did not settle.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] private struct ScrollBarInfo
    {
        public uint Size; public NativeRect Rectangle; public int LineButton, ThumbTop, ThumbBottom, Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public uint[] States;
    }
    [DllImport("user32.dll")] private static extern bool GetScrollBarInfo(nint window, int objectId, ref ScrollBarInfo info);
}

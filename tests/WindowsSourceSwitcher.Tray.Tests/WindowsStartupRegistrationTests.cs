using System.Runtime.ExceptionServices;
using System.Xml.Linq;
using System.Windows.Forms;
using WindowsSourceSwitcher.Tray;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class WindowsStartupRegistrationTests
{
    private const string Sid = "S-1-5-21-111-222-333-1001";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    [Fact]
    public void NewLoginTaskIsCurrentUserMinimizedAndDoesNotRequestElevation()
    {
        WithLauncher(path =>
        {
            var xml = WindowsStartupRegistration.CreateDefinition(path, Sid);
            var document = XDocument.Parse(xml);
            Assert.Equal(path, document.Descendants(Ns + "Command").Single().Value);
            Assert.Equal("--background", document.Descendants(Ns + "Arguments").Single().Value);
            Assert.Equal("LeastPrivilege", document.Descendants(Ns + "RunLevel").Single().Value);
            Assert.Equal("InteractiveToken", document.Descendants(Ns + "LogonType").Single().Value);
            Assert.All(document.Descendants(Ns + "UserId"), user => Assert.Equal(Sid, user.Value));
            Assert.Equal("PT0S", document.Descendants(Ns + "ExecutionTimeLimit").Single().Value);
            WindowsStartupRegistration.ValidateTask(xml, path, Sid);
            document.Descendants(Ns + "RunLevel").Single().Value = "HighestAvailable";
            WindowsStartupRegistration.ValidateTask(document.ToString(), path, Sid); // Existing permissions are compatible and retained.
        });
    }

    [Theory]
    [InlineData("path")]
    [InlineData("user")]
    [InlineData("arguments")]
    [InlineData("trigger")]
    [InlineData("extra-action")]
    public void ForeignOrIncompatibleTasksAreRejectedWithoutMutation(string change) => WithLauncher(path =>
    {
        var document = XDocument.Parse(WindowsStartupRegistration.CreateDefinition(path, Sid));
        if (change == "path") document.Descendants(Ns + "Command").Single().Value = @"C:\Other\SourceSwitcher.exe";
        if (change == "user") document.Descendants(Ns + "UserId").First().Value = "S-1-5-18";
        if (change == "arguments") document.Descendants(Ns + "Arguments").Single().Value = "--inspect test.json";
        if (change == "trigger") document.Descendants(Ns + "LogonTrigger").Single().Name = Ns + "BootTrigger";
        if (change == "extra-action") document.Descendants(Ns + "Actions").Single().Add(new XElement(Ns + "Exec"));
        Assert.Throws<InvalidOperationException>(() => WindowsStartupRegistration.ValidateTask(document.ToString(), path, Sid));
    });

    [Fact]
    public void MenuReadsActualStateAndFailedWritesDoNotLeaveAFakeCheckmark() => OnSta(async () =>
    {
        using var menu = new ContextMenuStrip();
        var backend = new FakeStartup { Enabled = true };
        var errors = new List<string>();
        var controller = new StartupMenuItem(menu, backend, errors.Add);
        menu.Items.Add(controller.Item);
        Assert.False(controller.Item.CheckOnClick);
        await controller.RefreshAsync(); Assert.True(controller.Item.Checked);
        await controller.ToggleAsync(); Assert.False(backend.Enabled); Assert.False(controller.Item.Checked);
        backend.Enabled = true;
        await controller.RefreshAsync(); Assert.True(controller.Item.Checked); // Windows was changed outside the app.
        backend.FailWrite = true;
        await controller.ToggleAsync(); Assert.True(controller.Item.Checked); Assert.Single(errors);
    });

    [Fact]
    public void ClosingMenuDuringStatusReadDoesNotTouchDisposedItems() => OnSta(async () =>
    {
        using var menu = new ContextMenuStrip();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeStartup { ReadGate = release.Task };
        var controller = new StartupMenuItem(menu, backend, _ => { });
        menu.Items.Add(controller.Item);
        var read = controller.RefreshAsync();
        menu.Dispose(); release.SetResult(true);
        await read;
    });

    private sealed class FakeStartup : IStartupRegistration
    {
        public bool Enabled, FailWrite;
        public Task<bool>? ReadGate;
        public bool ReadEnabled() => ReadGate?.GetAwaiter().GetResult() ?? Enabled;
        public void SetEnabled(bool enabled) { if (FailWrite) throw new UnauthorizedAccessException("Denied"); Enabled = enabled; }
    }
    private static void WithLauncher(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "source-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "SourceSwitcher.exe");
        File.WriteAllText(path, "test launcher; never executed");
        try { action(path); }
        finally { File.Delete(path); Directory.Delete(directory); }
    }
    private static void OnSta(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            using var form = new Form { Opacity = 0, ShowInTaskbar = false };
            form.Shown += async (_, _) => { try { await action(); } catch (Exception ex) { failure = ex; } finally { form.Close(); } };
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Startup menu test did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

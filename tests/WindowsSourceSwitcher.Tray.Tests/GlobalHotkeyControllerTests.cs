using WindowsSourceSwitcher.Tray;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class GlobalHotkeyControllerTests
{
    [Fact]
    public void DisabledByDefaultDoesNotCreateAWindowOrRegisterAnyShortcut()
    {
        var created = 0;
        var registrar = new FakeRegistrar();
        using var controller = new GlobalHotkeyController(registrar, () => { created++; return new FakeWindow(); });
        controller.SetEnabled(false);
        controller.Retry();
        Assert.Equal(0, created);
        Assert.Empty(registrar.Registered);
        Assert.Equal("Global hotkeys off.", controller.Status);
    }

    [Fact]
    public void EnableRegistersTheFixedShortcutsWithNoRepeatAndOwnWindow()
    {
        var registrar = new FakeRegistrar();
        var window = new FakeWindow();
        using var controller = new GlobalHotkeyController(registrar, () => window);
        controller.SetEnabled(true);
        Assert.Equal(6, registrar.Registered.Count);
        Assert.Equal([0x4Fu, 0x4Du, 0x25u, 0x27u, 0x52u, 0x46u], registrar.Registered.Select(item => item.VirtualKey));
        Assert.All(registrar.Registered, item =>
        {
            Assert.Equal(window.Handle, item.Window);
            Assert.Equal(0x4007u, item.Modifiers);
            Assert.InRange(item.Id, 0, 0xBFFF);
        });
        Assert.Contains("6/6 registered", controller.Status);
        controller.SetEnabled(true);
        Assert.Equal(6, registrar.Registered.Count);
    }

    [Fact]
    public void MatchingMessagesDispatchTheirActionsOnTheOwningThread()
    {
        var registrar = new FakeRegistrar();
        var window = new FakeWindow();
        using var controller = new GlobalHotkeyController(registrar, () => window);
        var invoked = new List<TrayHotkeyAction>();
        var threads = new List<int>();
        controller.Invoked += action => { invoked.Add(action); threads.Add(Environment.CurrentManagedThreadId); };
        controller.SetEnabled(true);
        foreach (var registration in registrar.Registered) window.Receive(registration.Id, 7, registration.VirtualKey);
        Assert.Equal(GlobalHotkeyController.Bindings.Select(binding => binding.Action), invoked);
        Assert.All(threads, thread => Assert.Equal(Environment.CurrentManagedThreadId, thread));
    }

    [Theory]
    [InlineData(-1, 7u, 0x4Fu)]
    [InlineData(123, 7u, 0x4Fu)]
    [InlineData(0x1000, 3u, 0x4Fu)]
    [InlineData(0x1000, 15u, 0x4Fu)]
    [InlineData(0x1000, 0x4007u, 0x4Fu)]
    [InlineData(0x1000, 7u, 0x4Du)]
    public void UnknownIdentifiersAndMismatchedMessageChordsDoNotDispatch(int id, uint modifiers, uint key)
    {
        var registrar = new FakeRegistrar();
        var window = new FakeWindow();
        using var controller = new GlobalHotkeyController(registrar, () => window);
        var invoked = 0;
        controller.Invoked += _ => invoked++;
        controller.SetEnabled(true);
        window.Receive(id, modifiers, key);
        Assert.Equal(0, invoked);
    }

    [Fact]
    public void ConflictsAreReportedPerActionWithoutDisablingSuccessfulShortcuts()
    {
        var registrar = new FakeRegistrar();
        registrar.ConflictingKeys.UnionWith([0x4Du, 0x27u]);
        var window = new FakeWindow();
        using var controller = new GlobalHotkeyController(registrar, () => window);
        var invoked = new List<TrayHotkeyAction>();
        controller.Invoked += invoked.Add;
        controller.SetEnabled(true);
        Assert.Contains("4/6 registered", controller.Status);
        Assert.Contains("Open mixer: unavailable (Win32 1409)", controller.Status);
        Assert.Contains("Next favorite: unavailable (Win32 1409)", controller.Status);
        foreach (var registration in registrar.Registered) window.Receive(registration.Id, 7, registration.VirtualKey);
        Assert.Equal([TrayHotkeyAction.OpenSources, TrayHotkeyAction.PreviousFavorite, TrayHotkeyAction.ReleaseMaster, TrayHotkeyAction.ToggleFullscreenPriority], invoked);
    }

    [Fact]
    public void RetryReleasesOldRegistrationsFirstUsesFreshIdsAndRecoversFromConflicts()
    {
        var registrar = new FakeRegistrar();
        registrar.ConflictingKeys.Add(0x4D);
        var window = new FakeWindow();
        using var controller = new GlobalHotkeyController(registrar, () => window);
        controller.SetEnabled(true);
        var old = registrar.Registered.ToArray();
        registrar.ConflictingKeys.Clear();
        controller.Retry();
        Assert.Contains("6/6 registered", controller.Status);
        Assert.Equal(5, registrar.Unregistered.Count);
        Assert.Equal(12, registrar.Registered.Count);
        Assert.DoesNotContain(registrar.Registered.Skip(6), next => old.Any(previous => previous.Id == next.Id));
        Assert.All(registrar.Operations.Skip(6).Take(5), operation => Assert.StartsWith("unregister:", operation));
        Assert.All(registrar.Operations.Skip(11), operation => Assert.StartsWith("register:", operation));
        Assert.Equal(6, registrar.LiveIds.Count);
    }

    [Fact]
    public void ReenabledAndRetriedRegistrationsIgnoreQueuedStaleMessages()
    {
        var registrar = new FakeRegistrar();
        var window = new FakeWindow();
        using var controller = new GlobalHotkeyController(registrar, () => window);
        var invoked = 0;
        controller.Invoked += _ => invoked++;
        controller.SetEnabled(true);
        var original = registrar.Registered[0];
        controller.SetEnabled(false);
        window.Receive(original.Id, 7, original.VirtualKey);
        controller.SetEnabled(true);
        window.Receive(original.Id, 7, original.VirtualKey);
        var reenabled = registrar.Registered[6];
        window.Receive(reenabled.Id, 7, reenabled.VirtualKey);
        controller.Retry();
        window.Receive(reenabled.Id, 7, reenabled.VirtualKey);
        window.Receive(registrar.Registered[12].Id, 7, original.VirtualKey);
        Assert.Equal(2, invoked);
    }

    [Fact]
    public void DisableReleasesEveryOwnedShortcutAndRetryStaysDisabled()
    {
        var registrar = new FakeRegistrar();
        var window = new FakeWindow();
        using var controller = new GlobalHotkeyController(registrar, () => window);
        controller.SetEnabled(true);
        controller.SetEnabled(false);
        Assert.Equal(6, registrar.Unregistered.Count);
        Assert.Empty(registrar.LiveIds);
        controller.Retry();
        controller.SetEnabled(false);
        Assert.Equal(6, registrar.Registered.Count);
        Assert.Equal(6, registrar.Unregistered.Count);
        Assert.Equal("Global hotkeys off.", controller.Status);
    }

    [Fact]
    public void FailedUnregisterIsInactiveTrackedAndRetriedWithoutRegisteringAnythingNew()
    {
        var registrar = new FakeRegistrar();
        var window = new FakeWindow();
        using var controller = new GlobalHotkeyController(registrar, () => window);
        var invoked = 0;
        controller.Invoked += _ => invoked++;
        controller.SetEnabled(true);
        var failed = registrar.Registered[0];
        registrar.FailingUnregisterIds.Add(failed.Id);
        controller.SetEnabled(false);
        Assert.Contains("unregister failed (Win32 5)", controller.Status);
        Assert.Equal(failed.Id, Assert.Single(registrar.LiveIds));
        window.Receive(failed.Id, 7, failed.VirtualKey);
        Assert.Equal(0, invoked);
        registrar.FailingUnregisterIds.Clear();
        controller.SetEnabled(false);
        Assert.Empty(registrar.LiveIds);
        Assert.Equal(7, registrar.Unregistered.Count);
        Assert.Equal(6, registrar.Registered.Count);
        Assert.Equal("Global hotkeys off.", controller.Status);
    }

    [Fact]
    public void AlreadyUnregisteredIdentifiersDoNotRemainOwned()
    {
        var registrar = new FakeRegistrar();
        var window = new FakeWindow();
        using var controller = new GlobalHotkeyController(registrar, () => window);
        controller.SetEnabled(true);
        registrar.LiveIds.Clear();
        controller.SetEnabled(false);
        controller.Retry();
        Assert.Equal(6, registrar.Unregistered.Count);
        Assert.Equal("Global hotkeys off.", controller.Status);
    }

    [Fact]
    public void WindowCreationFailureIsReportedAndExplicitRetryCanRecover()
    {
        var attempts = 0;
        var registrar = new FakeRegistrar();
        using var controller = new GlobalHotkeyController(registrar, () =>
        {
            if (attempts++ == 0) throw new InvalidOperationException("Window unavailable");
            return new FakeWindow();
        });
        controller.SetEnabled(true);
        Assert.Contains("0/6 registered", controller.Status);
        Assert.Contains("Window unavailable", controller.Status);
        controller.Retry();
        Assert.Contains("6/6 registered", controller.Status);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void RegistrationExceptionKeepsEarlierSuccessOwnedForCleanup()
    {
        var registrar = new FakeRegistrar { ThrowOnVirtualKey = 0x4D };
        var window = new FakeWindow();
        var controller = new GlobalHotkeyController(registrar, () => window);
        controller.SetEnabled(true);
        Assert.Contains("1/6 registered", controller.Status);
        controller.Dispose();
        Assert.Single(registrar.Unregistered);
        Assert.Empty(registrar.LiveIds);
        Assert.Equal(1, window.DisposeCalls);
    }

    [Fact]
    public void DisposeReleasesAllSuccessfulRegistrationsDestroysTheWindowAndIgnoresFurtherMessages()
    {
        var registrar = new FakeRegistrar();
        var window = new FakeWindow();
        var controller = new GlobalHotkeyController(registrar, () => window);
        var invoked = 0;
        controller.Invoked += _ => invoked++;
        controller.SetEnabled(true);
        controller.Dispose();
        controller.Dispose();
        Assert.Equal(6, registrar.Unregistered.Count);
        Assert.Empty(registrar.LiveIds);
        Assert.Equal(1, window.DisposeCalls);
        foreach (var registration in registrar.Registered) window.Receive(registration.Id, 7, registration.VirtualKey);
        Assert.Equal(0, invoked);
        Assert.Throws<ObjectDisposedException>(() => controller.SetEnabled(true));
        Assert.Throws<ObjectDisposedException>(controller.Retry);
    }

    [Fact]
    public void StatusAndActionConsumersCannotBreakRegistrationOrCleanup()
    {
        var registrar = new FakeRegistrar();
        var window = new FakeWindow();
        using var controller = new GlobalHotkeyController(registrar, () => window);
        controller.StatusChanged += () => throw new InvalidOperationException("Consumer closed");
        controller.Invoked += _ => throw new InvalidOperationException("Action unavailable");
        controller.SetEnabled(true);
        window.Receive(registrar.Registered[0].Id, 7, 0x4F);
        Assert.Contains("Hotkey action failed: Action unavailable", controller.Status);
        controller.SetEnabled(false);
        Assert.Empty(registrar.LiveIds);
    }

    [Fact]
    public void ForeignThreadCannotConfigureDisposeOrDispatchActions()
    {
        var registrar = new FakeRegistrar();
        var window = new FakeWindow();
        using var controller = new GlobalHotkeyController(registrar, () => window);
        var invoked = 0;
        controller.Invoked += _ => invoked++;
        controller.SetEnabled(true);
        Exception? configureFailure = null;
        Exception? disposeFailure = null;
        var thread = new Thread(() =>
        {
            try { controller.SetEnabled(false); } catch (Exception error) { configureFailure = error; }
            try { controller.Dispose(); } catch (Exception error) { disposeFailure = error; }
            window.Receive(registrar.Registered[0].Id, 7, 0x4F);
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(configureFailure);
        Assert.IsType<InvalidOperationException>(disposeFailure);
        Assert.Equal(0, invoked);
        Assert.Equal(6, registrar.LiveIds.Count);
    }

    private sealed record Registration(IntPtr Window, int Id, uint Modifiers, uint VirtualKey);

    private sealed class FakeRegistrar : IHotkeyRegistrar
    {
        public List<Registration> Registered { get; } = [];
        public List<int> Unregistered { get; } = [];
        public List<string> Operations { get; } = [];
        public HashSet<int> LiveIds { get; } = [];
        public HashSet<uint> ConflictingKeys { get; } = [];
        public HashSet<int> FailingUnregisterIds { get; } = [];
        public uint? ThrowOnVirtualKey { get; init; }

        public bool Register(IntPtr window, int id, uint modifiers, uint virtualKey, out int error)
        {
            Registered.Add(new(window, id, modifiers, virtualKey));
            Operations.Add("register:" + id);
            if (virtualKey == ThrowOnVirtualKey) throw new InvalidOperationException("Registration unavailable");
            error = ConflictingKeys.Contains(virtualKey) ? 1409 : 0;
            if (error != 0) return false;
            LiveIds.Add(id);
            return true;
        }

        public bool Unregister(IntPtr window, int id, out int error)
        {
            Unregistered.Add(id);
            Operations.Add("unregister:" + id);
            if (FailingUnregisterIds.Contains(id)) { error = 5; return false; }
            error = LiveIds.Remove(id) ? 0 : 1419;
            return error == 0;
        }
    }

    private sealed class FakeWindow : IHotkeyMessageWindow
    {
        public IntPtr Handle => new(12345);
        public event Action<int, uint, uint>? HotkeyReceived;
        public int DisposeCalls { get; private set; }
        public void Receive(int id, uint modifiers, uint virtualKey) => HotkeyReceived?.Invoke(id, modifiers, virtualKey);
        public void Dispose() => DisposeCalls++;
    }
}

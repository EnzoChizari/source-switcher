using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class AudioDeviceWatcherTests
{
    [Fact]
    public async Task NativeRegistrationAndCleanupHaveOneMtaOwner()
    {
        var registration = new FakeRegistration();
        var factoryThread = 0;
        var factoryApartment = ApartmentState.Unknown;
        var watcher = new AudioDeviceWatcher(() =>
        {
            factoryThread = Environment.CurrentManagedThreadId;
            factoryApartment = Thread.CurrentThread.GetApartmentState();
            return registration;
        });
        try
        {
            await Ready(watcher);
            Assert.True(watcher.IsReady);
            Assert.Null(watcher.LastError);
            Assert.False(watcher.Completion.IsCompleted);
            Assert.Equal(ApartmentState.MTA, factoryApartment);
        }
        finally { watcher.Dispose(); }

        await watcher.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(watcher.IsReady);
        Assert.Equal(new[] { "Register", "Unregister", "Dispose" }, registration.Operations.Select(item => item.Name));
        Assert.All(registration.Operations, operation =>
        {
            Assert.Equal(factoryThread, operation.ThreadId);
            Assert.Equal(ApartmentState.MTA, operation.Apartment);
        });
        Assert.Same(registration.Client, registration.UnregisteredClient);
    }

    [Fact]
    public async Task AllNativeKindsAreForwardedWithoutRunningHandlersOnCallbackThread()
    {
        var registration = new FakeRegistration();
        using var watcher = new AudioDeviceWatcher(() => registration);
        var changes = new ConcurrentQueue<AudioEndpointChange>();
        var deliveryThreads = new ConcurrentQueue<int>();
        watcher.Changed += change => { changes.Enqueue(change); deliveryThreads.Enqueue(Environment.CurrentManagedThreadId); };
        await Ready(watcher);

        var callbackThread = 0;
        await Task.Run(() =>
        {
            callbackThread = Environment.CurrentManagedThreadId;
            registration.Client!.OnDeviceAdded("added");
            registration.Client.OnDeviceRemoved("removed");
            registration.Client.OnDeviceStateChanged("state", DeviceState.Unplugged);
            registration.Client.OnDefaultDeviceChanged(DataFlow.Render, Role.Multimedia, "default");
            registration.Client.OnPropertyValueChanged("property", default);
        });
        await Until(() => deliveryThreads.Count == 5);

        Assert.Contains(new(AudioEndpointChangeKind.Added, "added"), changes);
        Assert.Contains(new(AudioEndpointChangeKind.Removed, "removed"), changes);
        Assert.Contains(new(AudioEndpointChangeKind.StateChanged, "state"), changes);
        Assert.Contains(new(AudioEndpointChangeKind.DefaultChanged, "default"), changes);
        Assert.Contains(new(AudioEndpointChangeKind.PropertyChanged, "property"), changes);
        Assert.All(deliveryThreads, thread => Assert.NotEqual(callbackThread, thread));
        Assert.All(deliveryThreads, thread => Assert.Equal(registration.OwnerThread, thread));
        Assert.Equal("Register", Assert.Single(registration.Operations).Name);
    }

    [Fact]
    public async Task ClearedDefaultHasNullEndpointWithoutQueryingOrSelectingAnotherDevice()
    {
        var registration = new FakeRegistration();
        using var watcher = new AudioDeviceWatcher(() => registration);
        var delivered = new TaskCompletionSource<AudioEndpointChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Changed += change => delivered.TrySetResult(change);
        await Ready(watcher);
        registration.Client!.OnDefaultDeviceChanged(DataFlow.Render, Role.Multimedia, null!);
        var change = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(AudioEndpointChangeKind.DefaultChanged, change.Kind);
        Assert.Null(change.EndpointId);
        Assert.Equal("Register", Assert.Single(registration.Operations).Name);
    }

    [Fact]
    public async Task DuplicatePendingCallbacksAreCoalesced()
    {
        var registration = new FakeRegistration();
        using var blocker = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        using var watcher = new AudioDeviceWatcher(() => registration);
        var changes = new ConcurrentQueue<AudioEndpointChange>();
        watcher.Changed += change =>
        {
            changes.Enqueue(change);
            if (change.EndpointId != "block") return;
            entered.Set();
            blocker.Wait(TimeSpan.FromSeconds(5));
        };
        await Ready(watcher);
        registration.Client!.OnDeviceAdded("block");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        try
        {
            for (var index = 0; index < 100; index++)
                registration.Client.OnDeviceStateChanged("same", DeviceState.Active);
        }
        finally { blocker.Set(); }
        await Until(() => changes.Any(change => change.EndpointId == "same"));
        Assert.Single(changes, change => change.EndpointId == "same");
    }

    [Fact]
    public async Task NotificationBurstIsBoundedAndRequestsFullRefreshIfIdentityIsDropped()
    {
        var registration = new FakeRegistration();
        using var blocker = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        using var watcher = new AudioDeviceWatcher(() => registration);
        var changes = new ConcurrentQueue<AudioEndpointChange>();
        watcher.Changed += change =>
        {
            changes.Enqueue(change);
            if (change.EndpointId != "block") return;
            entered.Set();
            blocker.Wait(TimeSpan.FromSeconds(5));
        };
        await Ready(watcher);
        registration.Client!.OnDeviceAdded("block");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        try
        {
            for (var index = 0; index < 2000; index++)
                registration.Client.OnPropertyValueChanged("endpoint-" + index, default);
        }
        finally { blocker.Set(); }
        await Until(() => changes.Any(change => change.EndpointId is null));
        Assert.Equal(258, changes.Count); // Blocker, 256 identities and one overflow refresh.
        Assert.Single(changes, change => change.EndpointId is null);
        Assert.Equal(AudioEndpointChangeKind.PropertyChanged, changes.Single(change => change.EndpointId is null).Kind);
    }

    [Fact]
    public async Task NativeCallbacksReturnWhileConsumerIsBusy()
    {
        var registration = new FakeRegistration();
        using var blocker = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        using var watcher = new AudioDeviceWatcher(() => registration);
        watcher.Changed += _ => { entered.Set(); blocker.Wait(TimeSpan.FromSeconds(5)); };
        await Ready(watcher);
        registration.Client!.OnDeviceAdded("first");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        try
        {
            await Task.Run(() => registration.Client.OnDeviceRemoved("second"))
                .WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(blocker.IsSet);
        }
        finally { blocker.Set(); }
    }

    [Fact]
    public async Task NoQueriesOrRepeatedRegistrationRunWhileIdle()
    {
        var registration = new FakeRegistration();
        using var watcher = new AudioDeviceWatcher(() => registration);
        await Ready(watcher);
        await Task.Delay(80);
        Assert.Equal("Register", Assert.Single(registration.Operations).Name);
        Assert.True(watcher.IsReady);
    }

    [Fact]
    public async Task AThrowingHandlerDoesNotEscapeIntoNativeCallbackOrStopAnotherHandler()
    {
        var registration = new FakeRegistration();
        using var watcher = new AudioDeviceWatcher(() => registration);
        var delivered = new TaskCompletionSource<AudioEndpointChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Changed += _ => throw new InvalidOperationException("UI handle closed");
        watcher.Changed += change => delivered.TrySetResult(change);
        await Ready(watcher);
        registration.Client!.OnDeviceRemoved("target");
        Assert.Equal("target", (await delivered.Task.WaitAsync(TimeSpan.FromSeconds(3))).EndpointId);
        Assert.Contains("UI handle closed", watcher.LastError);
        Assert.True(watcher.IsReady);
    }

    [Fact]
    public async Task DisposeInsideChangedDoesNotJoinOwnerOrDeliverFurtherHandlers()
    {
        var registration = new FakeRegistration();
        using var watcher = new AudioDeviceWatcher(() => registration);
        var laterHandlerCalls = 0;
        watcher.Changed += _ => watcher.Dispose();
        watcher.Changed += _ => Interlocked.Increment(ref laterHandlerCalls);
        await Ready(watcher);
        registration.Client!.OnDeviceAdded("target");
        await watcher.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, laterHandlerCalls);
        Assert.Equal(1, registration.Operations.Count(item => item.Name == "Unregister"));
        Assert.Equal(1, registration.Operations.Count(item => item.Name == "Dispose"));
    }

    [Fact]
    public async Task NotificationsAfterDisposeAreIgnored()
    {
        var registration = new FakeRegistration();
        var watcher = new AudioDeviceWatcher(() => registration);
        var deliveries = 0;
        watcher.Changed += _ => Interlocked.Increment(ref deliveries);
        await Ready(watcher);
        watcher.Dispose();
        await watcher.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        var callback = registration.Client!;
        callback.OnDeviceAdded("late");
        callback.OnDeviceRemoved("late");
        callback.OnDeviceStateChanged("late", DeviceState.Active);
        callback.OnDefaultDeviceChanged(DataFlow.Render, Role.Multimedia, "late");
        callback.OnPropertyValueChanged("late", default);
        Assert.Equal(0, deliveries);
        Assert.False(watcher.IsReady);
        Assert.Null(watcher.LastError);
    }

    [Fact]
    public async Task FactoryFailureIsReportedWithoutCrashingWorker()
    {
        using var watcher = new AudioDeviceWatcher(() => throw new COMException("Audio service unavailable"));
        await watcher.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(watcher.IsReady);
        Assert.Contains("Audio service unavailable", watcher.LastError);
    }

    [Fact]
    public async Task MissingRegistrationIsReportedWithoutNullDereference()
    {
        using var watcher = new AudioDeviceWatcher(() => null!);
        await watcher.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(watcher.IsReady);
        Assert.Contains("unavailable", watcher.LastError);
    }

    [Fact]
    public async Task FailedRegistrationStillDisposesOnOwnerWithoutUnregisteringUnsubscribedClient()
    {
        var registration = new FakeRegistration { RegisterError = new COMException("Registration denied") };
        using var watcher = new AudioDeviceWatcher(() => registration);
        await watcher.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { "Register", "Dispose" }, registration.Operations.Select(item => item.Name));
        Assert.False(watcher.IsReady);
        Assert.Contains("Registration denied", watcher.LastError);
        registration.Client!.OnDeviceAdded("late"); // A failed registration cannot retain queued work.
        Assert.All(registration.Operations, item => Assert.Equal(registration.OwnerThread, item.ThreadId));
    }

    [Fact]
    public async Task FailedUnregisterStillReleasesNativeEnumerator()
    {
        var registration = new FakeRegistration { UnregisterError = new COMException("Device service stopped") };
        using var watcher = new AudioDeviceWatcher(() => registration);
        await Ready(watcher);
        watcher.Dispose();
        await watcher.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { "Register", "Unregister", "Dispose" }, registration.Operations.Select(item => item.Name));
        Assert.Contains("Device service stopped", watcher.LastError);
        Assert.False(watcher.IsReady);
    }

    [Fact]
    public async Task CleanupFailureIsContainedAndCompletionStillFinishes()
    {
        var registration = new FakeRegistration { DisposeError = new COMException("Enumerator unavailable") };
        using var watcher = new AudioDeviceWatcher(() => registration);
        await Ready(watcher);
        watcher.Dispose();
        await watcher.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("Enumerator unavailable", watcher.LastError);
        Assert.False(watcher.IsReady);
    }

    [Fact]
    public async Task DisposeIsBoundedWhenNativeCleanupStallsAndCompletionTracksEventualRelease()
    {
        using var cleanupGate = new ManualResetEventSlim(false);
        var registration = new FakeRegistration { DisposeGate = cleanupGate };
        var watcher = new AudioDeviceWatcher(() => registration);
        try
        {
            await Ready(watcher);
            var timer = Stopwatch.StartNew();
            watcher.Dispose();
            timer.Stop();
            Assert.InRange(timer.Elapsed.TotalSeconds, 1.5, 3.5);
            Assert.False(watcher.Completion.IsCompleted);
            Assert.False(watcher.IsReady);
            Assert.Equal(1, registration.Operations.Count(item => item.Name == "Unregister"));
        }
        finally
        {
            cleanupGate.Set();
            watcher.Dispose();
            await watcher.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Equal(1, registration.Operations.Count(item => item.Name == "Dispose"));
    }

    [Fact]
    public async Task ConcurrentRepeatedDisposalUnregistersExactlyOnce()
    {
        var registration = new FakeRegistration();
        var watcher = new AudioDeviceWatcher(() => registration);
        await Ready(watcher);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(watcher.Dispose)));
        await watcher.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, registration.Operations.Count(item => item.Name == "Unregister"));
        Assert.Equal(1, registration.Operations.Count(item => item.Name == "Dispose"));
    }

    [Fact]
    public void NullFactoryIsRejectedBeforeStartingThread() =>
        Assert.Throws<ArgumentNullException>(() => new AudioDeviceWatcher(null!));

    private static async Task Ready(AudioDeviceWatcher watcher)
    {
        await Until(() => watcher.IsReady || watcher.Completion.IsCompleted);
        Assert.True(watcher.IsReady, watcher.LastError);
    }

    private static async Task Until(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), "Timed out waiting for endpoint watcher state.");
            await Task.Delay(5);
        }
    }

    private sealed record Operation(string Name, int ThreadId, ApartmentState Apartment);

    private sealed class FakeRegistration : IAudioDeviceNotificationRegistration
    {
        public ConcurrentQueue<Operation> Operations { get; } = new();
        public IMMNotificationClient? Client { get; private set; }
        public IMMNotificationClient? UnregisteredClient { get; private set; }
        public int OwnerThread { get; private set; }
        public Exception? RegisterError { get; init; }
        public Exception? UnregisterError { get; init; }
        public Exception? DisposeError { get; init; }
        public ManualResetEventSlim? DisposeGate { get; init; }

        public void Register(IMMNotificationClient notifications)
        {
            OwnerThread = Environment.CurrentManagedThreadId;
            Client = notifications;
            Record("Register");
            if (RegisterError is not null) throw RegisterError;
        }

        public void Unregister(IMMNotificationClient notifications)
        {
            Record("Unregister");
            UnregisteredClient = notifications;
            if (UnregisterError is not null) throw UnregisterError;
        }

        public void Dispose()
        {
            Record("Dispose");
            DisposeGate?.Wait(TimeSpan.FromSeconds(5));
            if (DisposeError is not null) throw DisposeError;
        }

        private void Record(string name) => Operations.Enqueue(new(name,
            Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));
    }
}

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class AudioSessionMeterMonitorTests
{
    private const string Endpoint = "output-one";
    private const string AppPath = @"C:\Apps\player.exe";
    private static readonly DateTimeOffset Started = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GroupUsesMaximumPeakInsteadOfSumAndPreservesMetadata()
    {
        var first = Session("first", .25f);
        var second = Session("second", .8f);
        using var store = new FakeStore(first, second);
        using var sampler = new AudioSessionMeterSampler(store);
        sampler.SetSources(Request(Census(first, second)));
        var frame = sampler.Sample(7, 19, 12345);
        var peak = Assert.Single(frame.Sources);
        Assert.Equal(.8f, peak.Peak);
        Assert.Null(peak.Error);
        Assert.Equal(2, peak.Sessions.Count);
        Assert.Equal(Endpoint, frame.EndpointId);
        Assert.Equal(7, frame.Generation);
        Assert.Equal(19, frame.Sequence);
        Assert.Equal(12345, frame.SampledAtTimestamp);
    }

    [Fact]
    public void ZeroIsAValidWindowsPeakAndIsDifferentFromUnavailable()
    {
        var session = Session("one", 0);
        using var sampler = Sampler(out _, session);
        var zero = Assert.Single(sampler.Sample(1, 1, 1).Sources);
        Assert.Equal(0f, zero.Peak);
        Assert.Null(zero.Error);
        session.Value = null;
        var unavailable = Assert.Single(sampler.Sample(1, 2, 2).Sources);
        Assert.Null(unavailable.Peak);
        Assert.NotNull(unavailable.Error);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(-.01f)]
    [InlineData(1.01f)]
    public void InvalidNativePeakNeverBecomesSilenceOrFullScale(float invalid)
    {
        using var sampler = Sampler(out _, Session("one", invalid));
        var peak = Assert.Single(sampler.Sample(1, 1, 1).Sources);
        Assert.Null(peak.Peak);
        Assert.NotNull(peak.Error);
    }

    [Fact]
    public void UnreadableMemberInvalidatesWholeGroupWithoutReturningPartialMaximum()
    {
        var first = Session("one", .8f);
        var second = Session("two", null);
        using var sampler = Sampler(out _, first, second);
        var peak = Assert.Single(sampler.Sample(1, 1, 1).Sources);
        Assert.Null(peak.Peak);
        Assert.NotNull(peak.Error);
    }

    [Fact]
    public void MissingMemberInvalidatesWholeGroup()
    {
        var first = Session("one", .8f);
        var second = Session("two", .2f);
        using var store = new FakeStore(first);
        using var sampler = new AudioSessionMeterSampler(store);
        sampler.SetSources(Request(Census(first, second)));
        var peak = Assert.Single(sampler.Sample(1, 1, 1).Sources);
        Assert.Null(peak.Peak);
        Assert.NotNull(peak.Error);
    }

    [Fact]
    public void OneSourceFailureDoesNotSuppressAnIndependentSource()
    {
        var first = Session("one", .8f);
        first.Error = new COMException("Device invalidated");
        var other = Session("other", .4f, @"C:\Apps\other.exe");
        using var sampler = Sampler(out _, first, other);
        var frame = sampler.Sample(1, 1, 1);
        Assert.Null(frame.Sources.Single(source => source.Identity == first.Identity.SourceIdentity).Peak);
        Assert.Equal(.4f, frame.Sources.Single(source => source.Identity == other.Identity.SourceIdentity).Peak);
    }

    [Fact]
    public void APreviouslyReadPeakIsNotReusedAfterFailure()
    {
        var session = Session("one", .8f);
        using var sampler = Sampler(out _, session);
        Assert.Equal(.8f, Assert.Single(sampler.Sample(1, 1, 1).Sources).Peak);
        session.Error = new COMException("Session expired");
        Assert.Null(Assert.Single(sampler.Sample(1, 2, 2).Sources).Peak);
    }

    [Fact]
    public void SamplingDoesNotReenumerateSessionsOrReopenEndpoint()
    {
        var session = Session("one", .3f);
        using var sampler = Sampler(out var store, session);
        var opened = Assert.Single(store.Endpoints);
        for (var index = 0; index < 100; index++) sampler.Sample(1, index, index);
        Assert.Equal(1, store.Opens);
        Assert.Equal(1, opened.SessionListReads);
        Assert.Equal(100, session.Reads);
    }

    [Theory]
    [InlineData("process")]
    [InlineData("start")]
    [InlineData("path")]
    [InlineData("instance")]
    [InlineData("endpoint")]
    public void ChangedSessionIdentityCannotInheritCachedMeter(string changed)
    {
        var session = Session("one", .3f);
        using var sampler = Sampler(out _, session);
        session.Identity = changed switch
        {
            "process" => session.Identity with { ProcessId = 234 },
            "start" => session.Identity with { ProcessStartedAtUtc = Started.AddSeconds(1) },
            "path" => session.Identity with { ExecutablePath = @"C:\Elsewhere\player.exe" },
            "instance" => session.Identity with { SessionInstanceId = "new-session" },
            _ => session.Identity with { EndpointId = "different-output" }
        };
        var peak = Assert.Single(sampler.Sample(1, 1, 1).Sources);
        Assert.Null(peak.Peak);
        Assert.NotNull(peak.Error);
        Assert.Equal(0, session.Reads);
    }

    [Fact]
    public void IdenticalVerifiedCensusesReuseLeasesButContinueReadingFreshPeaks()
    {
        var session = Session("one", .3f);
        using var sampler = Sampler(out var store, session);
        for (var index = 0; index < 100; index++)
        {
            session.Value = index / 100f;
            sampler.SetSources(Request(Census(session)));
            Assert.Equal(session.Value, Assert.Single(sampler.Sample(index, index, index).Sources).Peak);
        }
        Assert.Equal(1, store.Opens);
        Assert.Equal(100, session.Reads);
        Assert.Equal(0, Assert.Single(store.Endpoints).Disposals);
    }

    [Fact]
    public void FailedReadRequiresFreshBindingEvenWhenTheNextCensusHasTheSameIdentity()
    {
        var session = Session("one", .3f);
        using var sampler = Sampler(out var store, session);
        var previous = Assert.Single(store.Endpoints);
        session.Error = new COMException("Endpoint invalidated");
        Assert.Null(Assert.Single(sampler.Sample(1, 1, 1).Sources).Peak);
        session.Error = null;
        session.Value = .7f;
        store.BeforeOpen = () => Assert.Equal(1, previous.Disposals);
        sampler.SetSources(Request(Census(session)));
        Assert.Equal(2, store.Opens);
        Assert.Equal(.7f, Assert.Single(sampler.Sample(2, 2, 2).Sources).Peak);
    }

    [Fact]
    public void IncompleteBindingCanRecoverOnAnIdenticalCensusBeforeTheFirstSample()
    {
        var first = Session("one", .3f);
        var missing = Session("two", .8f);
        using var store = new FakeStore(first);
        using var sampler = new AudioSessionMeterSampler(store);
        sampler.SetSources(Request(Census(first, missing)));
        store.Current = [first, missing];
        sampler.SetSources(Request(Census(first, missing)));
        Assert.Equal(2, store.Opens);
        Assert.Equal(.8f, Assert.Single(sampler.Sample(2, 2, 2).Sources).Peak);
    }

    [Fact]
    public void StopReleasesReusedLeasesAndReopeningBindsAgain()
    {
        var session = Session("one", .3f);
        using var sampler = Sampler(out var store, session);
        sampler.SetSources(Request(Census(session)));
        var previous = Assert.Single(store.Endpoints);
        sampler.SetSources(null);
        Assert.Equal(1, previous.Disposals);
        sampler.SetSources(Request(Census(session)));
        Assert.Equal(2, store.Opens);
    }

    [Fact]
    public void WrongEndpointLeaseIsReleasedAndCannotSupplyPeaks()
    {
        var session = Session("one", .3f);
        using var store = new FakeStore(session) { ReturnedEndpoint = "different-output" };
        using var sampler = new AudioSessionMeterSampler(store);
        sampler.SetSources(Request(Census(session)));
        var frame = sampler.Sample(1, 1, 1);
        Assert.Null(Assert.Single(frame.Sources).Peak);
        Assert.NotNull(frame.Error);
        Assert.Equal(1, Assert.Single(store.Endpoints).Disposals);
        Assert.Equal(0, session.Reads);
    }

    [Fact]
    public void FreshCensusReleasesOldEndpointBeforeBindingNewSessionInstances()
    {
        var first = Session("one", .3f);
        using var store = new FakeStore(first);
        using var sampler = new AudioSessionMeterSampler(store);
        sampler.SetSources(Request(Census(first)));
        var oldEndpoint = Assert.Single(store.Endpoints);
        var second = Session("new-instance", .7f);
        store.Current = [second];
        store.BeforeOpen = () => Assert.Equal(1, oldEndpoint.Disposals);
        sampler.SetSources(Request(Census(second)));
        Assert.Equal(.7f, Assert.Single(sampler.Sample(2, 1, 1).Sources).Peak);
        Assert.Equal(0, first.Reads);
    }

    [Theory]
    [InlineData("shared")]
    [InlineData("no-start")]
    [InlineData("no-path")]
    [InlineData("wrong-source")]
    [InlineData("wrong-endpoint")]
    public void UnverifiedSessionDoesNotAcquireNativeMeterLease(string invalid)
    {
        var session = Session("one", .3f);
        session.Identity = invalid switch
        {
            "shared" => session.Identity with { IsSharedAcrossProcesses = true },
            "no-start" => session.Identity with { ProcessStartedAtUtc = null },
            "no-path" => session.Identity with { ExecutablePath = null },
            "wrong-source" => session.Identity with { SourceIdentity = "wrong" },
            _ => session.Identity with { EndpointId = "different-output" }
        };
        var census = Census(session);
        // The group remains pinned to the selected endpoint, independent of its member.
        census = census with { Sources = census.Sources.Select(source => source with { EndpointId = Endpoint }).ToArray() };
        using var store = new FakeStore(session);
        using var sampler = new AudioSessionMeterSampler(store);
        sampler.SetSources(Request(census));
        Assert.Null(Assert.Single(sampler.Sample(1, 1, 1).Sources).Peak);
        Assert.Equal(0, store.Opens);
    }

    [Fact]
    public void SystemSoundsHasExplicitIdentityWithoutInventedExecutableOwner()
    {
        var system = Session("system", .1f);
        system.Identity = new(Endpoint, "system-sounds", 0, null, null, "system", true);
        using var sampler = Sampler(out _, system);
        Assert.Equal(.1f, Assert.Single(sampler.Sample(1, 1, 1).Sources).Peak);
    }

    [Fact]
    public void ShareXIsExcludedBeforeOpeningMeterLeases()
    {
        var excluded = Session("capture", .6f, @"C:\Tools\ShareX.exe");
        var kept = Session("player", .2f);
        using var sampler = Sampler(out var store, excluded, kept);
        Assert.Equal(kept.Identity.SourceIdentity, Assert.Single(sampler.Sample(1, 1, 1).Sources).Identity);
        Assert.Equal(kept.Identity, Assert.Single(store.LastExpected!));
        Assert.Equal(0, excluded.Reads);
    }

    [Fact]
    public void PartialCensusDoesNotPretendItsKnownMembersAreTheCompleteAppPeak()
    {
        var session = Session("one", .3f);
        using var store = new FakeStore(session);
        using var sampler = new AudioSessionMeterSampler(store);
        sampler.SetSources(Request(Census(session) with { Error = "Some sessions changed while reading." }));
        Assert.Null(Assert.Single(sampler.Sample(1, 1, 1).Sources).Peak);
        Assert.Equal(0, store.Opens);
    }

    [Fact]
    public void CensusCollectionsAreCopiedBeforeAsynchronousBinding()
    {
        var original = Session("one", .3f);
        var sessions = new List<VolumeSessionIdentity> { original.Identity };
        var source = Census(original).Sources[0] with { Sessions = sessions };
        var rows = new List<AudioSessionVolumeSnapshot> { source };
        var request = Request(new(Endpoint, rows, null));
        sessions.Clear();
        rows.Clear();
        using var store = new FakeStore(original);
        using var sampler = new AudioSessionMeterSampler(store);
        sampler.SetSources(request);
        Assert.Equal(.3f, Assert.Single(sampler.Sample(1, 1, 1).Sources).Peak);
    }

    [Fact]
    public void SessionLimitIsBoundedAndOversizedGroupIsExplicitlyUnavailable()
    {
        var sessions = Enumerable.Range(0, 300).Select(index => Session(index.ToString(), .2f)).ToArray();
        var request = Request(Census(sessions));
        Assert.Equal(AudioSessionMeterMonitor.MaximumSessions, Assert.Single(request!.Sources).Sessions.Count);
        Assert.NotNull(request.Error);
        using var store = new FakeStore(sessions);
        using var sampler = new AudioSessionMeterSampler(store);
        sampler.SetSources(request);
        Assert.Null(Assert.Single(sampler.Sample(1, 1, 1).Sources).Peak);
        Assert.Equal(0, store.Opens);
    }

    [Fact]
    public void SourceLimitBoundsFrameAllocation()
    {
        var sessions = Enumerable.Range(0, 300).Select(index => Session(index.ToString(), .2f, $@"C:\Apps\player{index}.exe")).ToArray();
        var request = Request(Census(sessions));
        Assert.Equal(AudioSessionMeterMonitor.MaximumSources, request!.Sources.Count);
        Assert.NotNull(request.Error);
    }

    [Fact]
    public void StopReleasesLeasesAndRepeatedDisposeDoesNotDoubleRelease()
    {
        var session = Session("one", .3f);
        var sampler = Sampler(out var store, session);
        var endpoint = Assert.Single(store.Endpoints);
        sampler.SetSources(null);
        Assert.Equal(1, endpoint.Disposals);
        Assert.Empty(sampler.Sample(2, 1, 1).Sources);
        sampler.Dispose();
        sampler.Dispose();
        Assert.Equal(1, endpoint.Disposals);
        Assert.Equal(1, store.Disposals);
        Assert.Throws<ObjectDisposedException>(() => sampler.Sample(1, 1, 1));
    }

    [Fact]
    public void FailedBindingIsUnavailableAndRetriesOnlyWithNextCensus()
    {
        var session = Session("one", .3f);
        using var store = new FakeStore(session) { OpenError = new COMException("Unplugged") };
        using var sampler = new AudioSessionMeterSampler(store);
        sampler.SetSources(Request(Census(session)));
        for (var index = 0; index < 100; index++) Assert.Null(Assert.Single(sampler.Sample(1, index, index).Sources).Peak);
        Assert.Equal(1, store.Opens);
        store.OpenError = null;
        sampler.SetSources(Request(Census(session)));
        Assert.Equal(.3f, Assert.Single(sampler.Sample(2, 1, 1).Sources).Peak);
        Assert.Equal(2, store.Opens);
    }

    [Fact]
    public void WorkerRunsNativeOperationsOnSingleMtaThreadAndIdlesAfterStop()
    {
        var session = Session("one", .3f);
        using var store = new FakeStore(session);
        var received = new ConcurrentQueue<AudioMeterFrame>();
        using var observed = new AutoResetEvent(false);
        var factoryThread = 0;
        using var monitor = new AudioSessionMeterMonitor(frame => { received.Enqueue(frame); observed.Set(); },
            () => { factoryThread = Environment.CurrentManagedThreadId; return store; }, 10);
        Assert.False(observed.WaitOne(50));
        Assert.Equal(0, store.Opens);
        var generation = monitor.SetSources(Census(session));
        Assert.True(observed.WaitOne(3000));
        Assert.True(SpinWait.SpinUntil(() => session.Reads >= 3, 3000));
        monitor.Stop();
        Assert.True(SpinWait.SpinUntil(() => store.Endpoints.Single().Disposals == 1, 3000));
        var stoppedReads = session.Reads;
        var stoppedFrames = received.Count;
        Assert.False(observed.WaitOne(1) && received.Count > stoppedFrames);
        Thread.Sleep(50);
        Assert.Equal(stoppedReads, session.Reads);
        Assert.Equal(stoppedFrames, received.Count);
        monitor.Dispose();
        Assert.Equal(1, store.Disposals);
        Assert.All(store.Threads, thread => Assert.Equal(factoryThread, thread));
        Assert.All(store.Apartments, apartment => Assert.Equal(ApartmentState.MTA, apartment));
        Assert.All(received, frame => Assert.Equal(generation, frame.Generation));
        Assert.All(received, frame => Assert.True(frame.SampledAtTimestamp > 0));
    }

    [Fact]
    public void SlowOldSampleIsSuppressedAfterEndpointSwitch()
    {
        var first = Session("one", .3f);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        first.BeforeRead = () => { entered.Set(); Assert.True(release.Wait(3000)); };
        using var store = new FakeStore(first);
        var frames = new ConcurrentQueue<AudioMeterFrame>();
        using var observed = new AutoResetEvent(false);
        using var monitor = new AudioSessionMeterMonitor(frame => { frames.Enqueue(frame); observed.Set(); }, () => store, 10);
        monitor.SetSources(Census(first));
        Assert.True(entered.Wait(3000));
        var other = Session("other", .7f);
        other.Identity = other.Identity with { EndpointId = "output-two" };
        store.Current = [other];
        var second = Census(other) with { EndpointId = "output-two" };
        var generation = monitor.SetSources(second);
        release.Set();
        Assert.True(observed.WaitOne(3000));
        monitor.Stop();
        Assert.All(frames, frame => { Assert.Equal("output-two", frame.EndpointId); Assert.Equal(generation, frame.Generation); });
        Assert.All(frames, frame => Assert.Equal(.7f, Assert.Single(frame.Sources).Peak));
    }

    [Fact]
    public void CallbackCanStopAndDisposeItsOwnMonitor()
    {
        var session = Session("one", .3f);
        using var store = new FakeStore(session);
        using var cleaned = new ManualResetEventSlim();
        store.OnDispose = cleaned.Set;
        AudioSessionMeterMonitor? monitor = null;
        monitor = new AudioSessionMeterMonitor(_ => monitor!.Dispose(), () => store, 10);
        monitor.SetSources(Census(session));
        Assert.True(cleaned.Wait(3000));
        monitor.Dispose();
        Assert.Equal(1, store.Disposals);
        Assert.Equal(1, Assert.Single(store.Endpoints).Disposals);
    }

    [Fact]
    public void StoreCreationFailurePublishesUnknownAndRetriesOnlyAfterFreshCensus()
    {
        var session = Session("one", .3f);
        using var store = new FakeStore(session);
        using var observed = new AutoResetEvent(false);
        var frames = new ConcurrentQueue<AudioMeterFrame>();
        var attempts = 0;
        using var monitor = new AudioSessionMeterMonitor(frame => { frames.Enqueue(frame); observed.Set(); }, () =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new NotSupportedException("Unavailable adapter");
            return store;
        }, 10);
        monitor.SetSources(Census(session));
        Assert.True(observed.WaitOne(3000));
        var failed = Assert.Single(frames);
        Assert.Null(Assert.Single(failed.Sources).Peak);
        Assert.NotNull(failed.Error);
        Thread.Sleep(40);
        Assert.Equal(1, attempts);
        var generation = monitor.SetSources(Census(session));
        Assert.True(observed.WaitOne(3000));
        Assert.Contains(frames, frame => frame.Generation == generation && frame.Sources.Single().Peak == .3f);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void UnexpectedSampleFailureIsContainedAndBackendCanRecoverAtNextCensus()
    {
        var session = Session("one", .3f);
        session.Error = new NotSupportedException("Unexpected adapter failure");
        using var firstStore = new FakeStore(session);
        using var secondStore = new FakeStore(session);
        using var observed = new AutoResetEvent(false);
        var frames = new ConcurrentQueue<AudioMeterFrame>();
        var attempts = 0;
        using var monitor = new AudioSessionMeterMonitor(frame => { frames.Enqueue(frame); observed.Set(); },
            () => Interlocked.Increment(ref attempts) == 1 ? firstStore : secondStore, 10);
        monitor.SetSources(Census(session));
        Assert.True(observed.WaitOne(3000));
        Assert.Null(Assert.Single(Assert.Single(frames).Sources).Peak);
        Assert.Equal(1, firstStore.Disposals);
        session.Error = null;
        var generation = monitor.SetSources(Census(session));
        Assert.True(observed.WaitOne(3000));
        Assert.Contains(frames, frame => frame.Generation == generation && frame.Sources.Single().Peak == .3f);
    }

    [Fact]
    public void ConsumerExceptionCannotTerminateMeterWorkerOrAudioController()
    {
        var session = Session("one", .3f);
        using var store = new FakeStore(session);
        using var secondFrame = new ManualResetEventSlim();
        var callbacks = 0;
        using var monitor = new AudioSessionMeterMonitor(_ =>
        {
            if (Interlocked.Increment(ref callbacks) == 1) throw new NotSupportedException("Consumer failure");
            secondFrame.Set();
        }, () => store, 10);
        monitor.SetSources(Census(session));
        Assert.True(secondFrame.Wait(3000));
        Assert.True(callbacks >= 2);
    }

    [Fact]
    public void DisposalCompletionIsSignaledEvenIfAdapterCleanupFails()
    {
        var session = Session("one", .3f);
        using var observed = new ManualResetEventSlim();
        var store = new FakeStore(session) { OnDispose = () => throw new NotSupportedException("Cleanup failure") };
        var monitor = new AudioSessionMeterMonitor(_ => observed.Set(), () => store, 10);
        monitor.SetSources(Census(session));
        Assert.True(observed.Wait(3000));
        monitor.Dispose();
        Assert.True(monitor.Completion.IsCompletedSuccessfully);
        Assert.Equal(1, store.Disposals);
        // Late UI refreshes and repeated shutdown remain safe after the wait handle closes.
        monitor.SetSources(Census(session));
        monitor.Stop();
        monitor.Dispose();
    }

    private static FakeSession Session(string instance, float? peak, string path = AppPath) => new(
        new(Endpoint, SourceIdentity.FromExecutablePath(path), 100, Started, path, instance, false), peak);
    private static AudioSessionVolumeCensus Census(params FakeSession[] sessions) => new(Endpoint,
        sessions.GroupBy(session => session.Identity.SourceIdentity).Select(group => new AudioSessionVolumeSnapshot(
            group.First().Identity.EndpointId, group.Key, Path.GetFileNameWithoutExtension(group.First().Identity.ExecutablePath ?? "System sounds"),
            1, false, false, false, group.Select(session => session.Identity).ToArray(), true, true, null)).ToArray(), null);
    private static AudioSessionMeterMonitor.MeterRequest? Request(AudioSessionVolumeCensus census) => AudioSessionMeterMonitor.MeterRequest.From(census);
    private static AudioSessionMeterSampler Sampler(out FakeStore store, params FakeSession[] sessions)
    {
        store = new FakeStore(sessions);
        var sampler = new AudioSessionMeterSampler(store);
        sampler.SetSources(Request(Census(sessions)));
        return sampler;
    }

    private sealed class FakeSession(VolumeSessionIdentity identity, float? value) : IAudioSessionMeterSession
    {
        private int _reads;
        public VolumeSessionIdentity Identity { get; set; } = identity;
        public float? Value { get; set; } = value;
        public Exception? Error { get; set; }
        public Action? BeforeRead { get; set; }
        public int Reads => Volatile.Read(ref _reads);
        public float? ReadPeak()
        { Interlocked.Increment(ref _reads); BeforeRead?.Invoke(); if (Error is not null) throw Error; return Value; }
    }

    private sealed class FakeStore(params FakeSession[] sessions) : IAudioSessionMeterStore
    {
        public FakeSession[] Current { get; set; } = sessions;
        public List<FakeEndpoint> Endpoints { get; } = [];
        public List<int> Threads { get; } = [];
        public List<ApartmentState> Apartments { get; } = [];
        public IReadOnlyList<VolumeSessionIdentity>? LastExpected { get; private set; }
        public string? ReturnedEndpoint { get; set; }
        public Exception? OpenError { get; set; }
        public Action? BeforeOpen { get; set; }
        public Action? OnDispose { get; set; }
        public int Opens { get; private set; }
        public int Disposals { get; private set; }
        public IAudioSessionMeterEndpoint Open(string endpointId, IReadOnlyList<VolumeSessionIdentity> expected)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            Apartments.Add(Thread.CurrentThread.GetApartmentState());
            Opens++;
            LastExpected = expected;
            BeforeOpen?.Invoke();
            if (OpenError is not null) throw OpenError;
            var endpoint = new FakeEndpoint(this, ReturnedEndpoint ?? endpointId, Current);
            Endpoints.Add(endpoint);
            return endpoint;
        }
        public void Record()
        { Threads.Add(Environment.CurrentManagedThreadId); Apartments.Add(Thread.CurrentThread.GetApartmentState()); }
        public void Dispose() { Record(); Disposals++; OnDispose?.Invoke(); }
    }

    private sealed class FakeEndpoint(FakeStore store, string endpointId, FakeSession[] sessions) : IAudioSessionMeterEndpoint
    {
        private int _disposals;
        public int Disposals => Volatile.Read(ref _disposals);
        public int SessionListReads { get; private set; }
        public string EndpointId => endpointId;
        public IReadOnlyList<IAudioSessionMeterSession> Sessions { get { store.Record(); SessionListReads++; return sessions; } }
        public string? Error => null;
        public void Dispose() { store.Record(); Interlocked.Increment(ref _disposals); }
    }
}

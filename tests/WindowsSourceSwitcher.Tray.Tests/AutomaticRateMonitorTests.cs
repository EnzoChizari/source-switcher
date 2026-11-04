using System.Threading.Channels;
using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class AutomaticRateMonitorTests
{
    [Fact]
    public async Task RefreshRereadsMissedTrackMetadataAndCoalescesRepeatedClicks()
    {
        var provider = new FakeTrackSource();
        var resolver = new FakeResolver(44100, 96000);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        await using var monitor = new AutomaticRateMonitor(new DiagnosticsLogger(), provider, provider.Snapshot, resolver,
            async token =>
            {
                Interlocked.Increment(ref reads);
                entered.SetResult();
                await release.Task.WaitAsync(token);
                provider.Publish(Track() with { Title = "New song" });
                return true;
            });
        var observed = Channel.CreateUnbounded<NativeRateObservation>();
        monitor.RateObserved += (_, value) => observed.Writer.TryWrite(value);
        await monitor.StartAsync(CancellationToken.None);
        var source = Source();
        monitor.SelectSource(source, 0);
        provider.Publish(Track());
        var old = await ReadRateAsync(observed.Reader, 44100);

        var retry = monitor.ResolveCurrentAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(monitor.IsMetadataCurrent(old.Generation, source.Identity, source.SessionToken, 0));
        Assert.False(monitor.IsEvidenceCurrent(old.Generation, source.Identity, source.SessionToken, 0));
        Assert.Same(retry, monitor.ResolveCurrentAsync(CancellationToken.None));
        Assert.Equal(1, resolver.Calls);
        release.SetResult();
        await retry.WaitAsync(TimeSpan.FromSeconds(5));
        var fresh = await ReadRateAsync(observed.Reader, 96000);

        Assert.Equal(1, reads);
        Assert.Equal(2, resolver.Calls);
        Assert.Contains("New song", fresh.Evidence);
        Assert.True(monitor.IsMetadataCurrent(fresh.Generation, source.Identity, source.SessionToken, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedMetadataReadHoldsWithoutQueryingStaleTrackAndNextRetryRecovers(bool throws)
    {
        var provider = new FakeTrackSource();
        var resolver = new FakeResolver(44100, 192000);
        var succeed = false;
        await using var monitor = new AutomaticRateMonitor(new DiagnosticsLogger(), provider, provider.Snapshot, resolver,
            _ => succeed ? Task.FromResult(true) : throws
                ? Task.FromException<bool>(new InvalidOperationException("Owned read failure")) : Task.FromResult(false));
        var observed = Channel.CreateUnbounded<NativeRateObservation>();
        monitor.RateObserved += (_, value) => observed.Writer.TryWrite(value);
        await monitor.StartAsync(CancellationToken.None);
        var source = Source();
        monitor.SelectSource(source, 0);
        provider.Publish(Track());
        var old = await ReadRateAsync(observed.Reader, 44100);

        await monitor.ResolveCurrentAsync(CancellationToken.None);
        monitor.SelectSource(source, 0); // Periodic census must not revive the old estimate.
        Assert.False(monitor.IsMetadataCurrent(old.Generation, source.Identity, source.SessionToken, 0));
        Assert.Equal(1, resolver.Calls);
        succeed = true;
        await monitor.ResolveCurrentAsync(CancellationToken.None);
        var fresh = await ReadRateAsync(observed.Reader, 192000);
        Assert.Equal(2, resolver.Calls);
        Assert.True(monitor.IsEvidenceCurrent(fresh.Generation, source.Identity, source.SessionToken, 0));
    }

    [Fact]
    public async Task DisposalCancelsBlockedMetadataReadWithoutAStaleCatalogRequest()
    {
        var provider = new FakeTrackSource();
        var resolver = new FakeResolver(44100);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new AutomaticRateMonitor(new DiagnosticsLogger(), provider, provider.Snapshot, resolver,
            async token => { entered.SetResult(); await never.Task.WaitAsync(token); return true; });
        await monitor.StartAsync(CancellationToken.None);
        monitor.SelectSource(Source(), 0);
        var retry = monitor.ResolveCurrentAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await monitor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await retry.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task ExplicitRetryFetchesFreshCatalogDataAndReplacesCachedEstimate()
    {
        var provider = new FakeTrackSource();
        var resolver = new FakeResolver(44100, 96000);
        await using var monitor = CreateMonitor(provider, resolver);
        var observed = Channel.CreateUnbounded<NativeRateObservation>();
        monitor.RateObserved += (_, value) => observed.Writer.TryWrite(value);
        await monitor.StartAsync(CancellationToken.None);
        monitor.SelectSource(Source(), 0);
        provider.Publish(Track());
        await ReadRateAsync(observed.Reader, 44100);
        Assert.Equal(1, resolver.Calls);

        await monitor.ResolveCurrentAsync(CancellationToken.None);
        await ReadRateAsync(observed.Reader, 96000);
        Assert.Equal(2, resolver.Calls);

        // A normal census/replay can use the new cache entry. Only explicit
        // Retry bypasses it; repeatedly reading a row does not cause networking.
        provider.Publish(Track());
        await ReadRateAsync(observed.Reader, 96000);
        Assert.Equal(2, resolver.Calls);
    }

    [Fact]
    public async Task FailedExplicitRetryCannotRestoreTheEarlierCachedRate()
    {
        var provider = new FakeTrackSource();
        var resolver = new FakeResolver(44100, null, 96000);
        await using var monitor = CreateMonitor(provider, resolver);
        var observed = Channel.CreateUnbounded<NativeRateObservation>();
        monitor.RateObserved += (_, value) => observed.Writer.TryWrite(value);
        await monitor.StartAsync(CancellationToken.None);
        monitor.SelectSource(Source(), 0);
        provider.Publish(Track());
        await ReadRateAsync(observed.Reader, 44100);

        await monitor.ResolveCurrentAsync(CancellationToken.None);
        Assert.Equal(2, resolver.Calls);
        monitor.SelectSource(Source() with { SessionToken = "restarted-session" }, 0);
        var fresh = await ReadRateAsync(observed.Reader, 96000);

        Assert.Equal(3, resolver.Calls);
        Assert.Equal("restarted-session", fresh.SessionToken);
    }

    [Fact]
    public async Task PausedTrackMetadataRemainsAvailableButCannotAuthorizeAnOutputWrite()
    {
        var provider = new FakeTrackSource { Playback = MediaTransportPlaybackStatus.Paused };
        var resolver = new FakeResolver(96000);
        await using var monitor = CreateMonitor(provider, resolver);
        var observed = Channel.CreateUnbounded<NativeRateObservation>();
        monitor.RateObserved += (_, value) => observed.Writer.TryWrite(value);
        await monitor.StartAsync(CancellationToken.None);
        var source = Source() with { IsActive = false };
        monitor.SelectSource(source, 0);
        provider.Publish(Track());
        var estimate = await ReadRateAsync(observed.Reader, 96000);

        Assert.True(monitor.IsMetadataCurrent(estimate.Generation, source.Identity, source.SessionToken, 0));
        Assert.False(monitor.IsEvidenceCurrent(estimate.Generation, source.Identity, source.SessionToken, 0));
        provider.Playback = MediaTransportPlaybackStatus.Playing;
        Assert.True(monitor.IsMetadataCurrent(estimate.Generation, source.Identity, source.SessionToken, 0));
        Assert.False(monitor.IsEvidenceCurrent(estimate.Generation, source.Identity, source.SessionToken, 0));
        monitor.SelectSource(source with { IsActive = true }, 0);
        Assert.True(monitor.IsEvidenceCurrent(estimate.Generation, source.Identity, source.SessionToken, 0));
        Assert.Equal(1, resolver.Calls);
        provider.Playback = MediaTransportPlaybackStatus.Changing;
        Assert.False(monitor.IsMetadataCurrent(estimate.Generation, source.Identity, source.SessionToken, 0));
        Assert.False(monitor.IsEvidenceCurrent(estimate.Generation, source.Identity, source.SessionToken, 0));
    }

    [Fact]
    public async Task CacheCapacityEvictsOnlyLeastRecentlyUsedTrackAndRetryStillInvalidatesIt()
    {
        var provider = new FakeTrackSource();
        var rates = Enumerable.Repeat<int?>(44100, 258).Concat(new int?[] { null, 96000 }).ToArray();
        var resolver = new FakeResolver(rates);
        await using var monitor = CreateMonitor(provider, resolver);
        var observed = Channel.CreateUnbounded<NativeRateObservation>();
        monitor.RateObserved += (_, value) => observed.Writer.TryWrite(value);
        await monitor.StartAsync(CancellationToken.None);
        monitor.SelectSource(Source(), 0);
        async Task Visit(int number, int rate = 44100)
        {
            provider.Publish(Track() with { Title = "Song " + number });
            await ReadRateAsync(observed.Reader, rate);
        }
        for (var i = 0; i < 256; i++) await Visit(i);
        Assert.Equal(256, resolver.Calls);
        await Visit(0); // Keep this old, recently replayed track hot.
        await Visit(256);
        await Visit(0);
        await Visit(255);
        Assert.Equal(257, resolver.Calls); // Capacity growth must not flush all entries.
        await Visit(1); // The one genuinely oldest entry needs a fresh lookup.
        Assert.Equal(258, resolver.Calls);
        await monitor.ResolveCurrentAsync(CancellationToken.None); // Failed retry removes old estimate.
        Assert.Equal(259, resolver.Calls);
        await Visit(1, 96000);
        Assert.Equal(260, resolver.Calls);
    }

    private static AutomaticRateMonitor CreateMonitor(FakeTrackSource provider, FakeResolver resolver)
        => new(new DiagnosticsLogger(), provider, provider.Snapshot, resolver);

    private static async Task<NativeRateObservation> ReadRateAsync(ChannelReader<NativeRateObservation> observations, int rate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var value = await observations.ReadAsync(timeout.Token);
            if (value.SampleRateHz == rate) return value;
        }
    }

    private static PlatformAudioSource Source() => new("APPLE", "Apple Music",
        @"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.2.3.4_x64__nzyj5cx40ttqa\AppleMusic.exe",
        [123], true, true, "current-session");

    private static TrackSnapshot Track() => new("AppleInc.AppleMusicWin!App", null, "Song", "Artist", "Album", "test", DateTimeOffset.UtcNow);

    private sealed class FakeTrackSource : ITrackSource
    {
        private TrackSnapshot? _track;
        public MediaTransportPlaybackStatus Playback { get; set; } = MediaTransportPlaybackStatus.Playing;
        public event EventHandler<TrackSnapshot>? TrackChanged;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Publish(TrackSnapshot track)
        {
            _track = track;
            TrackChanged?.Invoke(this, track);
        }
        public MediaSessionSnapshot Snapshot() => MediaSessionSnapshot.CreateUnavailable() with
        {
            SourceAppUserModelId = _track?.SourceAppUserModelId ?? "",
            Title = _track?.Title, Artist = _track?.Artist, Album = _track?.Album,
            PlaybackStatus = Playback
        };
    }

    private sealed class FakeResolver(params int?[] rates) : IFormatResolver
    {
        private int _calls;
        public string Name => "Owned test catalog";
        public int Calls => Volatile.Read(ref _calls);
        public Task<ResolvedAudioFormat?> ResolveAsync(TrackSnapshot track, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            var rate = rates[index];
            return Task.FromResult(rate is { } value
                ? new ResolvedAudioFormat(value, 24, ResolutionConfidence.Exact, AudioFormatSource.CatalogManifest, "Owned fixture estimate")
                : null);
        }
    }
}

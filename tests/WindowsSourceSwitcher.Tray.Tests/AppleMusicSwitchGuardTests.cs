using WindowsLosslessSwitcher.Models;
using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;
namespace WindowsSourceSwitcher.Tray.Tests;
public sealed class AppleMusicSwitchGuardTests
{
    [Fact]
    public async Task HealthyStreamWritesOnceWithoutFixedRecoveryDelay()
    {
        var clock = new Clock(); var session = new Session(); var writes = 0;
        var result = await clock.Guard.RunAsync(session, () => true, () => true, () => .1f,
            () => { writes++; return Applied; }, default);
        Assert.Equal(1, writes); Assert.True(result.Applied?.Success); Assert.True(result.PlaybackObserved);
        Assert.InRange(clock.Elapsed.TotalMilliseconds, 120, 300);
    }
    [Theory]
    [InlineData(MediaTransportPlaybackStatus.Paused)]
    [InlineData(MediaTransportPlaybackStatus.Unknown)]
    public async Task PausedOrUnknownPlayerHoldsOutput(MediaTransportPlaybackStatus status)
    {
        var result = await new Clock().Guard.RunAsync(new Session { Status = status }, () => true,
            () => true, () => .1f, MustNotWrite, default);
        Assert.Null(result.Applied); Assert.True(result.RetryOnActivity);
    }
    [Fact]
    public async Task MissingSessionOrMetadataHoldsOutput()
    {
        foreach (var session in new Session?[] { null, new() { Key = "" }, new() { Attached = false } })
        {
            var result = await new Clock().Guard.RunAsync(session, () => true, () => true, () => .1f, MustNotWrite, default);
            Assert.Null(result.Applied); Assert.True(result.RetryOnActivity);
        }
    }
    [Theory]
    [InlineData(false, .1f)]
    [InlineData(null, .1f)]
    [InlineData(true, 0f)]
    [InlineData(true, float.NaN)]
    [InlineData(true, float.PositiveInfinity)]
    public async Task ColdOrUnobservableStreamNeverInvalidated(bool? active, float peak)
    {
        var clock = new Clock();
        var result = await clock.Guard.RunAsync(new Session(), () => true, () => active, () => peak, MustNotWrite, default);
        Assert.Null(result.Applied); Assert.True(result.RetryOnActivity);
        Assert.True(result.WaitingForSignal);
        Assert.Equal(AppleMusicSwitchGuard.StartupTimeout, clock.Elapsed);
    }
    [Fact]
    public async Task SilentAdvancingPlayerCanRaiseDepthAndReseekOnceAfterReadback()
    {
        var clock = new Clock();
        var session = new Session { AcceptReseek = true };
        var writes = 0;
        var readbackPublished = false;
        session.OnReseek = () => Assert.True(readbackPublished);
        var result = await clock.Guard.RunAsync(session, () => true, () => true,
            () => session.Reseeks > 0 ? .1f : 0f,
            () => { writes++; return Applied; }, default, () => readbackPublished = true,
            allowSilentDepthIncrease: true);
        Assert.Equal(1, writes);
        Assert.Equal(1, session.Reseeks);
        Assert.True(result.PlaybackObserved);
    }

    [Theory]
    [InlineData(false, 0f, true)]
    [InlineData(true, 0.0001f, true)]
    [InlineData(true, 0f, false)]
    public async Task ColdRaiseStillNeedsMeasurableZeroOnAnActiveAdvancingTrack(bool active, float peak, bool advances)
    {
        var session = new Session { Advance = advances };
        var result = await new Clock().Guard.RunAsync(session, () => true, () => active, () => peak,
            MustNotWrite, default, allowSilentDepthIncrease: true);
        Assert.True(result.WaitingForSignal);
        Assert.Equal(0, session.Reseeks);
    }

    [Fact]
    public async Task FailedFormatWriteNeverReseeks()
    {
        var session = new Session();
        var result = await new Clock().Guard.RunAsync(session, () => true, () => true, () => 0f,
            () => new(false, false, "Rejected"), default, allowSilentDepthIncrease: true);
        Assert.False(result.Applied?.Success);
        Assert.Equal(0, session.Reseeks);
    }

    [Fact]
    public async Task SilentRaiseThatRestoresAudioDoesNotReseek()
    {
        var session = new Session();
        var wrote = false;
        var result = await new Clock().Guard.RunAsync(session, () => true, () => true, () => wrote ? .1f : 0f,
            () => { wrote = true; return Applied; }, default, allowSilentDepthIncrease: true);
        Assert.True(result.PlaybackObserved);
        Assert.Equal(0, session.Reseeks);
    }

    [Fact]
    public async Task AlreadyAppliedFormatDoesNotTriggerReseek()
    {
        var session = new Session();
        var result = await new Clock().Guard.RunAsync(session, () => true, () => true, () => 0f,
            () => Applied with { Changed = false }, default, allowSilentDepthIncrease: true);
        Assert.False(result.PlaybackObserved);
        Assert.Equal(0, session.Reseeks);
    }

    [Fact]
    public async Task RejectedReseekIsNeverRepeated()
    {
        var session = new Session();
        var result = await new Clock().Guard.RunAsync(session, () => true, () => true, () => 0f,
            () => Applied, default, allowSilentDepthIncrease: true);
        Assert.False(result.PlaybackObserved);
        Assert.Equal(1, session.Reseeks);
    }

    [Fact]
    public async Task HealthySixteenBitPlaybackDoesNotUseCompatibilityDepth()
    {
        var result = await new Clock().Guard.RunAsync(new Session(), () => true, () => true, () => .1f,
            () => Applied with { Changed = false }, default, recoverSilent: MustNotWrite);
        Assert.True(result.PlaybackObserved);
        Assert.False(result.Applied?.Changed);
    }

    [Fact]
    public async Task SilentSixteenBitPlaybackUsesCompatibilityDepthInsteadOfTheNormalTarget()
    {
        var recovered = false;
        var result = await new Clock().Guard.RunAsync(new Session(), () => true, () => true,
            () => recovered ? .1f : 0f, MustNotWrite, default,
            recoverSilent: () => { recovered = true; return Applied; });
        Assert.True(result.PlaybackObserved);
        Assert.True(recovered);
    }

    [Fact]
    public async Task FailedDepthReductionRestoresOnceWhileTheOriginalTrackStillPlays()
    {
        var wrote = false;
        var restores = 0;
        var publications = 0;
        var result = await new Clock().Guard.RunAsync(new Session(), () => true, () => true,
            () => wrote ? 0f : .1f, () => { wrote = true; return Applied; }, default,
            () => publications++, restorePrevious: () => { restores++; return Applied; });
        Assert.True(result.Restoration?.Success);
        Assert.Equal(1, restores);
        Assert.Equal(2, publications);
    }

    [Fact]
    public async Task LowerDepthThatRestoresSignalDoesNotRestoreTheOldFormat()
    {
        var result = await new Clock().Guard.RunAsync(new Session(), () => true, () => true, () => .1f,
            () => Applied, default, restorePrevious: MustNotWrite);
        Assert.True(result.PlaybackObserved);
        Assert.Null(result.Restoration);
    }
    [Theory]
    [InlineData("track")]
    [InlineData("session")]
    [InlineData("master")]
    [InlineData("pause")]
    public async Task UserChangeBeforeWriteCancelsWithoutTransportCommands(string change)
    {
        var session = new Session(); var current = true;
        var clock = new Clock(_ => Change(session, change, ref current));
        var result = await clock.Guard.RunAsync(session, () => current, () => true, () => .1f, MustNotWrite, default);
        Assert.Null(result.Applied);
    }
    [Theory]
    [InlineData("track")]
    [InlineData("session")]
    [InlineData("master")]
    [InlineData("pause")]
    public async Task UserChangeAfterWritePreservesActualWriteResult(string change)
    {
        var session = new Session { Advance = false }; var current = true; var wrote = false;
        var clock = new Clock(_ => { if (wrote) Change(session, change, ref current); });
        var result = await clock.Guard.RunAsync(session, () => current, () => true, () => .1f,
            () => { wrote = true; return Applied; }, default);
        Assert.True(result.Applied?.Success); Assert.False(result.PlaybackObserved);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SignalAndTimelineAreBothRequiredForRecovery(bool missingTimeline)
    {
        var session = new Session { Advance = !missingTimeline }; var wrote = false;
        var clock = new Clock();
        var result = await clock.Guard.RunAsync(session, () => true, () => true,
            () => wrote && !missingTimeline ? 0f : .1f, () => { wrote = true; return Applied; }, default);
        Assert.True(result.Applied?.Success); Assert.False(result.PlaybackObserved);
        Assert.InRange(clock.Elapsed.TotalSeconds, 6.12, 6.24);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationReportsWhetherWriteActuallyHappened(bool afterWrite)
    {
        using var stop = new CancellationTokenSource(); var wrote = false;
        var clock = new Clock(_ => { if (!afterWrite || wrote) stop.Cancel(); });
        var result = await clock.Guard.RunAsync(new Session { Advance = false }, () => true, () => true, () => .1f,
            () => { wrote = true; return Applied; }, stop.Token);
        Assert.Equal(afterWrite, result.Applied?.Success == true); Assert.False(result.PlaybackObserved);
    }
    [Fact]
    public async Task ChangedEvidenceAndRejectedWriteAreDistinct()
    {
        var stale = await new Clock().Guard.RunAsync(new Session(), () => true, () => true, () => .1f, () => null, default);
        Assert.Null(stale.Applied); Assert.True(stale.RetryOnActivity);
        var failed = await new Clock().Guard.RunAsync(new Session(), () => true, () => true, () => .1f,
            () => new(false, false, "Rejected"), default);
        Assert.False(failed.Applied?.Success); Assert.False(failed.RetryOnActivity);
    }
    private static ApplyResult Applied => new(true, true, "Test readback confirmed");
    [Fact]
    public async Task EndpointConfirmationIsPublishedBeforeSlowPlaybackConfirmation()
    {
        var clock = new Clock();
        TimeSpan? publishedAt = null;
        var result = await clock.Guard.RunAsync(new Session { Advance = false }, () => true, () => true, () => .1f,
            () => Applied, default, () => publishedAt = clock.Elapsed);
        Assert.Equal(TimeSpan.FromMilliseconds(120), publishedAt);
        Assert.True(result.Applied?.Success);
        Assert.False(result.PlaybackObserved);
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(6));
    }
    [Fact]
    public async Task RejectedWriteDoesNotPublishEndpointConfirmation()
    {
        var published = false;
        var result = await new Clock().Guard.RunAsync(new Session(), () => true, () => true, () => .1f,
            () => new(false, false, "Rejected"), default, () => published = true);
        Assert.False(published);
        Assert.False(result.Applied?.Success);
    }
    private static ApplyResult MustNotWrite() => throw new InvalidOperationException("Unexpected endpoint write");
    private static void Change(Session session, string change, ref bool current)
    {
        if (change == "track") session.Key = "replacement";
        if (change == "session") session.Attached = false;
        if (change == "master") current = false;
        if (change == "pause") session.Status = MediaTransportPlaybackStatus.Paused;
    }
    private sealed class Clock
    {
        public TimeSpan Elapsed;
        public AppleMusicSwitchGuard Guard { get; }
        public Clock(Action<TimeSpan>? advance = null) => Guard = new((value, _) =>
        { Elapsed += value; advance?.Invoke(Elapsed); return Task.CompletedTask; }, () => Elapsed);
    }
    private sealed class Session : IAppleSwitchSession
    {
        public string Key = "track";
        public bool Attached = true, Advance = true;
        public MediaTransportPlaybackStatus Status = MediaTransportPlaybackStatus.Playing;
        public TimeSpan Position;
        public int Reseeks;
        public bool AcceptReseek;
        public Action? OnReseek;
        public Task<AppleSwitchState?> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Advance) Position += TimeSpan.FromMilliseconds(100);
            return Task.FromResult(Attached ? new AppleSwitchState(Key, Status, Position) : null);
        }
        public Task<bool> TryReseekAsync(AppleSwitchState state, Func<bool> contextCurrent, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.True(contextCurrent());
            Assert.Equal(Key, state.TrackKey);
            Reseeks++;
            OnReseek?.Invoke();
            return Task.FromResult(AcceptReseek);
        }
    }
}

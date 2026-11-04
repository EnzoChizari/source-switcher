using System;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public class SessionContinuityTrackerTests
{
    [Fact]
    public void InitialState_IsNone_WithDefaultDetail()
    {
        var tracker = new SessionContinuityTracker();
        Assert.Equal(SessionContinuityState.None, tracker.State);
        Assert.Equal("No recent format change.", tracker.Detail);
    }

    [Fact]
    public void Begin_RejectsInvalidArguments()
    {
        var tracker = new SessionContinuityTracker();

        Assert.Throws<ArgumentException>(() => tracker.Begin("", "app.exe", 1, 96000, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => tracker.Begin("   ", "app.exe", 1, 96000, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => tracker.Begin("ep1", "", 1, 96000, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Begin("ep1", "app.exe", 1, 0, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Begin("ep1", "app.exe", 1, -44100, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Begin("ep1", "app.exe", 1, 96000, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Begin_CapturesContext_AndSetsWaiting()
    {
        var tracker = new SessionContinuityTracker();
        var update = tracker.Begin("ep1", "player.exe", 42, 96000, TimeSpan.FromSeconds(10));

        Assert.Equal(SessionContinuityState.Waiting, tracker.State);
        Assert.Equal(SessionContinuityState.Waiting, update.State);
        Assert.Contains("96 kHz", update.Detail);
        Assert.Equal(tracker.Detail, update.Detail);
    }

    [Fact]
    public void ActiveImmediate_TransitionsToObservedImmediately()
    {
        var tracker = new SessionContinuityTracker();
        tracker.Begin("ep1", "player.exe", 1, 192000, TimeSpan.FromSeconds(1));

        var update = tracker.Observe("ep1", "player.exe", 1, endpointAvailable: true, censusReliable: true, sourceActive: true, TimeSpan.FromSeconds(1.2));

        Assert.NotNull(update);
        Assert.Equal(SessionContinuityState.Observed, update.State);
        Assert.Equal(SessionContinuityState.Observed, tracker.State);
        Assert.Contains("192 kHz", update.Detail);
        Assert.Contains("audibility unverified", update.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InactiveGrace_And_3sBoundary()
    {
        var tracker = new SessionContinuityTracker();
        tracker.Begin("ep1", "player.exe", 1, 44100, TimeSpan.FromSeconds(10));

        // Grace period before 3s
        var u1 = tracker.Observe("ep1", "player.exe", 1, true, true, false, TimeSpan.FromSeconds(11));
        Assert.Null(u1);
        Assert.Equal(SessionContinuityState.Waiting, tracker.State);

        var u2 = tracker.Observe("ep1", "player.exe", 1, true, true, false, TimeSpan.FromSeconds(12.9));
        Assert.Null(u2);
        Assert.Equal(SessionContinuityState.Waiting, tracker.State);

        // Reaches 3s boundary
        var u3 = tracker.Observe("ep1", "player.exe", 1, true, true, false, TimeSpan.FromSeconds(13.0));
        Assert.NotNull(u3);
        Assert.Equal(SessionContinuityState.Missing, u3.State);
        Assert.Equal(SessionContinuityState.Missing, tracker.State);
        Assert.Contains("No active Windows session observed after", u3.Detail);
        Assert.Contains("the player may be paused, closed or restarting. Audibility is unverified.", u3.Detail);
        Assert.Contains("44.1 kHz", u3.Detail);

        // Duplicate suppression
        var u4 = tracker.Observe("ep1", "player.exe", 1, true, true, false, TimeSpan.FromSeconds(14.0));
        Assert.Null(u4);
        Assert.Equal(SessionContinuityState.Missing, tracker.State);
    }

    [Fact]
    public void UnreliableCensus_HoldsWaiting_ThenSetsUnknown()
    {
        var tracker = new SessionContinuityTracker();
        tracker.Begin("ep1", "player.exe", 1, 48000, TimeSpan.FromSeconds(5));

        // Within 3s: hold waiting
        var u1 = tracker.Observe("ep1", "player.exe", 1, true, censusReliable: false, sourceActive: false, TimeSpan.FromSeconds(6));
        Assert.Null(u1);
        Assert.Equal(SessionContinuityState.Waiting, tracker.State);

        // At 3s: transitions to Unknown
        var u2 = tracker.Observe("ep1", "player.exe", 1, true, censusReliable: false, sourceActive: false, TimeSpan.FromSeconds(8));
        Assert.NotNull(u2);
        Assert.Equal(SessionContinuityState.Unknown, u2.State);
        Assert.Contains("Session census unreliable", u2.Detail);

        // Duplicate suppression
        var u3 = tracker.Observe("ep1", "player.exe", 1, true, censusReliable: false, sourceActive: false, TimeSpan.FromSeconds(9));
        Assert.Null(u3);
    }

    [Fact]
    public void UnavailableEndpoint_SetsUnknown_NeverMissing()
    {
        var tracker = new SessionContinuityTracker();
        tracker.Begin("ep1", "player.exe", 1, 96000, TimeSpan.FromSeconds(0));

        var update = tracker.Observe("ep1", "player.exe", 1, endpointAvailable: false, censusReliable: true, sourceActive: false, TimeSpan.FromSeconds(5));
        Assert.NotNull(update);
        Assert.Equal(SessionContinuityState.Unknown, update.State);
        Assert.NotEqual(SessionContinuityState.Missing, update.State);
        Assert.Contains("Endpoint unavailable", update.Detail);
    }

    [Fact]
    public void LaterRecovery_FromMissingOrUnknown_ToObserved()
    {
        var tracker = new SessionContinuityTracker();
        tracker.Begin("ep1", "player.exe", 1, 96000, TimeSpan.FromSeconds(0));

        tracker.Observe("ep1", "player.exe", 1, true, true, false, TimeSpan.FromSeconds(3.5));
        Assert.Equal(SessionContinuityState.Missing, tracker.State);

        var recovered = tracker.Observe("ep1", "player.exe", 1, true, true, true, TimeSpan.FromSeconds(4.0));
        Assert.NotNull(recovered);
        Assert.Equal(SessionContinuityState.Observed, recovered.State);
        Assert.Equal(SessionContinuityState.Observed, tracker.State);
    }

    [Fact]
    public void OnceObserved_DoesNotDowngrade_OnLaterPause()
    {
        var tracker = new SessionContinuityTracker();
        tracker.Begin("ep1", "player.exe", 1, 96000, TimeSpan.FromSeconds(0));

        tracker.Observe("ep1", "player.exe", 1, true, true, true, TimeSpan.FromSeconds(0.5));
        Assert.Equal(SessionContinuityState.Observed, tracker.State);

        // Later pause / inactive
        var update = tracker.Observe("ep1", "player.exe", 1, true, true, false, TimeSpan.FromSeconds(5.0));
        Assert.Null(update);
        Assert.Equal(SessionContinuityState.Observed, tracker.State);
    }

    [Theory]
    [InlineData("ep2", "player.exe", 1L)]
    [InlineData("ep1", "other.exe", 1L)]
    [InlineData("ep1", "player.exe", 2L)]
    public void ContextMismatch_CancelsTracking(string endpoint, string source, long rev)
    {
        var tracker = new SessionContinuityTracker();
        tracker.Begin("ep1", "player.exe", 1, 96000, TimeSpan.FromSeconds(0));

        var update = tracker.Observe(endpoint, source, rev, true, true, true, TimeSpan.FromSeconds(1));
        Assert.NotNull(update);
        Assert.Equal(SessionContinuityState.Cancelled, update.State);
        Assert.Equal(SessionContinuityState.Cancelled, tracker.State);

        // Further observations return null
        var next = tracker.Observe("ep1", "player.exe", 1, true, true, true, TimeSpan.FromSeconds(2));
        Assert.Null(next);
    }

    [Fact]
    public void ContextMatch_IsCaseInsensitive_ForEndpointAndSource()
    {
        var tracker = new SessionContinuityTracker();
        tracker.Begin("ep1", "player.exe", 1, 96000, TimeSpan.FromSeconds(0));

        var update = tracker.Observe("EP1", "PLAYER.EXE", 1, true, true, true, TimeSpan.FromSeconds(1));
        Assert.NotNull(update);
        Assert.Equal(SessionContinuityState.Observed, update.State);
    }

    [Fact]
    public void ReversedClock_CancelsTracking()
    {
        var tracker = new SessionContinuityTracker();
        tracker.Begin("ep1", "player.exe", 1, 96000, TimeSpan.FromSeconds(5));

        tracker.Observe("ep1", "player.exe", 1, true, true, false, TimeSpan.FromSeconds(6));

        var update = tracker.Observe("ep1", "player.exe", 1, true, true, false, TimeSpan.FromSeconds(4));
        Assert.NotNull(update);
        Assert.Equal(SessionContinuityState.Cancelled, update.State);
        Assert.Equal(SessionContinuityState.Cancelled, tracker.State);
    }

    [Fact]
    public void NewBegin_ReplacesEarlierChecks()
    {
        var tracker = new SessionContinuityTracker();
        tracker.Begin("ep1", "player.exe", 1, 96000, TimeSpan.FromSeconds(0));
        tracker.Observe("ep1", "player.exe", 1, true, true, false, TimeSpan.FromSeconds(4));
        Assert.Equal(SessionContinuityState.Missing, tracker.State);

        var resetUpdate = tracker.Begin("ep2", "player2.exe", 2, 48000, TimeSpan.FromSeconds(10));
        Assert.Equal(SessionContinuityState.Waiting, resetUpdate.State);
        Assert.Equal(SessionContinuityState.Waiting, tracker.State);
        Assert.Contains("48 kHz", resetUpdate.Detail);

        // Old context now cancels
        var oldObserve = tracker.Observe("ep1", "player.exe", 1, true, true, true, TimeSpan.FromSeconds(11));
        Assert.NotNull(oldObserve);
        Assert.Equal(SessionContinuityState.Cancelled, oldObserve.State);
    }

    [Fact]
    public void Cancel_IsIdempotent_AndHaltsObserve()
    {
        var tracker = new SessionContinuityTracker();
        tracker.Begin("ep1", "player.exe", 1, 96000, TimeSpan.FromSeconds(0));

        var c1 = tracker.Cancel();
        Assert.NotNull(c1);
        Assert.Equal(SessionContinuityState.Cancelled, c1.State);

        var c2 = tracker.Cancel();
        Assert.Null(c2);

        var obs = tracker.Observe("ep1", "player.exe", 1, true, true, true, TimeSpan.FromSeconds(1));
        Assert.Null(obs);
    }

    [Fact]
    public void CancellingBeforeAnyFormatChangeDoesNotInventAHistoryEvent()
    {
        var tracker = new SessionContinuityTracker();
        Assert.Null(tracker.Cancel());
        Assert.Equal(SessionContinuityState.None, tracker.State);
        Assert.Equal("No recent format change.", tracker.Detail);
    }

    [Fact]
    public void Observe_WhenNone_ReturnsNull()
    {
        var tracker = new SessionContinuityTracker();
        var obs = tracker.Observe("ep1", "player.exe", 1, true, true, true, TimeSpan.FromSeconds(1));
        Assert.Null(obs);
    }
}

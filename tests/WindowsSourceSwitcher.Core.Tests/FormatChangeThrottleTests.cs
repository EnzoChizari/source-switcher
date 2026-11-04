using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Core.Tests;

public sealed class FormatChangeThrottleTests
{
    [Fact]
    public void Ordinary_changes_are_immediate_but_a_burst_is_bounded()
    {
        var guard = new FormatChangeThrottle();
        for (var index = 0; index < 6; index++)
        {
            Assert.Equal(TimeSpan.Zero, guard.RetryAfter(TimeSpan.FromSeconds(index)));
            guard.RecordAttempt(TimeSpan.FromSeconds(index));
        }
        Assert.Equal(TimeSpan.FromSeconds(4), guard.RetryAfter(TimeSpan.FromSeconds(6)));
        Assert.Throws<InvalidOperationException>(() => guard.RecordAttempt(TimeSpan.FromSeconds(6)));
        Assert.Equal(TimeSpan.Zero, guard.RetryAfter(TimeSpan.FromSeconds(10)));
        guard.RecordAttempt(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(1), guard.RetryAfter(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Explicit_retry_reset_and_clock_discontinuity_cannot_leave_permanent_block()
    {
        var guard = new FormatChangeThrottle(1);
        guard.RecordAttempt(TimeSpan.FromSeconds(100));
        Assert.True(guard.RetryAfter(TimeSpan.FromSeconds(101)) > TimeSpan.Zero);
        guard.Reset();
        Assert.Equal(TimeSpan.Zero, guard.RetryAfter(TimeSpan.FromSeconds(101)));
        guard.RecordAttempt(TimeSpan.FromSeconds(101));
        Assert.Equal(TimeSpan.Zero, guard.RetryAfter(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Invalid_limits_and_time_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FormatChangeThrottle(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FormatChangeThrottle(1, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FormatChangeThrottle().RetryAfter(TimeSpan.FromSeconds(-1)));
    }
}

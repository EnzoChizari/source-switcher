using System.ComponentModel;
using System.Runtime.InteropServices;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class StreamObserverRecoveryTests
{
    [Fact]
    public void RetriesBackOffAndCapWithoutBeingResetByRepeatedRefreshes()
    {
        var gate = new StreamObserverRecovery();
        var now = TimeSpan.Zero;
        foreach (var delay in new[] { 1, 2, 4, 8, 16, 30, 30 })
        {
            Assert.False(gate.ShouldRetry(false, true, now));
            Assert.False(gate.ShouldRetry(false, true, now + TimeSpan.FromSeconds(delay) - TimeSpan.FromTicks(1)));
            now += TimeSpan.FromSeconds(delay);
            Assert.True(gate.ShouldRetry(false, true, now));
        }
    }

    [Fact]
    public void HealthyObserverIsNeverRestartedAndBriefRecoveryKeepsTheBackoff()
    {
        var gate = new StreamObserverRecovery();
        Assert.False(gate.ShouldRetry(false, true, TimeSpan.Zero));
        Assert.True(gate.ShouldRetry(false, true, TimeSpan.FromSeconds(1)));
        Assert.False(gate.ShouldRetry(true, true, TimeSpan.FromSeconds(1)));
        Assert.False(gate.ShouldRetry(true, true, TimeSpan.FromSeconds(20)));
        Assert.False(gate.ShouldRetry(false, true, TimeSpan.FromSeconds(21)));
        Assert.False(gate.ShouldRetry(false, true, TimeSpan.FromSeconds(22)));
        Assert.True(gate.ShouldRetry(false, true, TimeSpan.FromSeconds(23)));
    }

    [Fact]
    public void ThirtySecondsHealthyResetsTheNextDelay()
    {
        var gate = new StreamObserverRecovery();
        gate.ShouldRetry(false, true, TimeSpan.Zero);
        Assert.True(gate.ShouldRetry(false, true, TimeSpan.FromSeconds(1)));
        gate.ShouldRetry(true, true, TimeSpan.FromSeconds(1));
        gate.ShouldRetry(true, true, TimeSpan.FromSeconds(31));
        Assert.False(gate.ShouldRetry(false, true, TimeSpan.FromSeconds(32)));
        Assert.True(gate.ShouldRetry(false, true, TimeSpan.FromSeconds(33)));
    }

    [Fact]
    public void PermissionFailureCancelsPendingRetryUntilAnExplicitReset()
    {
        var gate = new StreamObserverRecovery();
        gate.ShouldRetry(false, true, TimeSpan.Zero);
        Assert.False(gate.ShouldRetry(false, false, TimeSpan.FromSeconds(1)));
        Assert.False(gate.ShouldRetry(false, false, TimeSpan.FromDays(1)));
        Assert.Contains("Permission", gate.Status);
        gate.Reset();
        Assert.False(gate.ShouldRetry(false, true, TimeSpan.Zero));
        Assert.True(gate.ShouldRetry(false, true, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ExplicitResetDropsAnOldDeadlineAndFailureHistory()
    {
        var gate = new StreamObserverRecovery();
        gate.ShouldRetry(false, true, TimeSpan.Zero);
        gate.ShouldRetry(false, true, TimeSpan.FromSeconds(1));
        gate.ShouldRetry(false, true, TimeSpan.FromSeconds(2));
        gate.Reset();
        Assert.False(gate.ShouldRetry(false, true, TimeSpan.FromSeconds(2)));
        Assert.True(gate.ShouldRetry(false, true, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void ReversedClockDoesNotImmediatelyRetryOrCountAsHealthyTime()
    {
        var gate = new StreamObserverRecovery();
        gate.ShouldRetry(false, true, TimeSpan.FromSeconds(50));
        Assert.False(gate.ShouldRetry(false, true, TimeSpan.Zero));
        Assert.True(gate.ShouldRetry(false, true, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.ShouldRetry(false, true, TimeSpan.FromTicks(-1)));
    }

    [Fact]
    public void PermissionClassificationDoesNotTurnOtherWin32FailuresIntoPermanentBlocks()
    {
        Assert.True(WasapiStreamRateMonitor.IsAccessDenied(new UnauthorizedAccessException()));
        Assert.True(WasapiStreamRateMonitor.IsAccessDenied(new Win32Exception(5)));
        Assert.True(WasapiStreamRateMonitor.IsAccessDenied(new COMException("Access denied", unchecked((int)0x80070005))));
        Assert.False(WasapiStreamRateMonitor.IsAccessDenied(new Win32Exception(6)));
        Assert.False(WasapiStreamRateMonitor.IsAccessDenied(new InvalidOperationException("Observer stopped")));
    }
}

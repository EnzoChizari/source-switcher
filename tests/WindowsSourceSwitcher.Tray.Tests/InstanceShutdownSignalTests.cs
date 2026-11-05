using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class InstanceShutdownSignalTests
{
    [Fact]
    public void Local_requests_signal_only_the_addressed_listener_and_disposal_removes_it()
    {
        var name = @"Local\SourceSwitcher.Test." + Guid.NewGuid().ToString("N");
        using var received = new AutoResetEvent(false);
        using (var listener = new InstanceShutdownSignal(() => received.Set(), name))
        {
            Assert.False(InstanceShutdownSignal.Request(name + ".other"));
            Assert.False(received.WaitOne(0));
            Assert.True(InstanceShutdownSignal.Request(name));
            Assert.True(received.WaitOne(TimeSpan.FromSeconds(3)));
            Assert.True(InstanceShutdownSignal.Request(name));
            Assert.True(received.WaitOne(TimeSpan.FromSeconds(3)));
        }
        Assert.False(InstanceShutdownSignal.Request(name));
    }
}

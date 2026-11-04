using System.Drawing;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class FullscreenAppMonitorTests
{
    [Theory]
    [InlineData(0, 0, 1920, 1080, true)]
    [InlineData(0, 30, 1920, 1050, false)] // Maximized with title bar.
    [InlineData(0, 0, 1920, 1040, false)] // Work area with taskbar.
    [InlineData(-2, -2, 1924, 1084, true)]
    [InlineData(-3, 0, 1923, 1080, false)]
    [InlineData(0, 0, 3840, 1080, false)] // Spanning two monitors is not a match.
    [InlineData(0, 0, 0, 0, false)]
    public void Only_full_monitor_client_area_qualifies(int x, int y, int width, int height, bool expected)
        => Assert.Equal(expected, FullscreenAppMonitor.CoversMonitor(new(x, y, width, height), new(0, 0, 1920, 1080)));

    [Fact]
    public void Secondary_monitor_negative_coordinates_are_supported()
    {
        var monitor = new Rectangle(-2560, -200, 2560, 1440);
        Assert.True(FullscreenAppMonitor.CoversMonitor(monitor, monitor));
        Assert.False(FullscreenAppMonitor.CoversMonitor(monitor, Rectangle.Empty));
    }
}

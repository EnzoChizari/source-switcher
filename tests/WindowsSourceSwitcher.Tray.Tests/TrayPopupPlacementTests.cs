using System;
using System.Drawing;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public class TrayPopupPlacementTests
{
    [Fact]
    public void Calculate_NormalPlacement_AlignsBottomRightWithMargin()
    {
        var workArea = new Rectangle(0, 0, 1920, 1080);
        var preferredSize = new Size(300, 200);
        const int margin = 12;

        var bounds = TrayPopupPlacement.Calculate(workArea, preferredSize, margin);

        Assert.Equal(new Rectangle(1608, 868, 300, 200), bounds);
        Assert.True(workArea.Contains(bounds));
    }

    [Fact]
    public void Calculate_NegativeOriginWorkArea_CalculatesCorrectBoundsInsideWorkArea()
    {
        var workArea = new Rectangle(-1920, -1080, 1920, 1080);
        var preferredSize = new Size(300, 200);
        const int margin = 12;

        var bounds = TrayPopupPlacement.Calculate(workArea, preferredSize, margin);

        Assert.Equal(new Rectangle(-312, -212, 300, 200), bounds);
        Assert.True(workArea.Contains(bounds));
    }

    [Fact]
    public void Calculate_OversizedWindow_ShrinksToAvailableInsetArea()
    {
        var workArea = new Rectangle(0, 0, 100, 100);
        var preferredSize = new Size(200, 300);
        const int margin = 10;

        var bounds = TrayPopupPlacement.Calculate(workArea, preferredSize, margin);

        Assert.Equal(new Rectangle(10, 10, 80, 80), bounds);
        Assert.True(workArea.Contains(bounds));
    }

    [Fact]
    public void Calculate_ExcessiveMargin_ClampedProperly()
    {
        var workArea = new Rectangle(0, 0, 10, 20);
        var preferredSize = new Size(5, 5);
        const int margin = 12;

        var bounds = TrayPopupPlacement.Calculate(workArea, preferredSize, margin);

        // min(10, 20) = 10, max margin = (10 - 1) / 2 = 4
        // effective margin = 4, available width = 10 - 8 = 2, available height = 20 - 8 = 12
        // width = min(5, 2) = 2, height = min(5, 12) = 5
        // x = 10 - 4 - 2 = 4, y = 20 - 4 - 5 = 11
        Assert.Equal(new Rectangle(4, 11, 2, 5), bounds);
        Assert.True(workArea.Contains(bounds));
    }

    [Fact]
    public void Calculate_OneByOneWorkArea_ReturnsOneByOneBounds()
    {
        var workArea = new Rectangle(5, 15, 1, 1);
        var preferredSize = new Size(10, 10);
        const int margin = 12;

        var bounds = TrayPopupPlacement.Calculate(workArea, preferredSize, margin);

        // min(1, 1) = 1, max margin = (1 - 1) / 2 = 0
        // available = 1x1, width = 1, height = 1
        // x = 6 - 0 - 1 = 5, y = 16 - 0 - 1 = 15
        Assert.Equal(new Rectangle(5, 15, 1, 1), bounds);
        Assert.True(workArea.Contains(bounds));
    }

    [Theory]
    [InlineData(0, 100, 50, 50, 10)]
    [InlineData(-10, 100, 50, 50, 10)]
    [InlineData(100, 0, 50, 50, 10)]
    [InlineData(100, -10, 50, 50, 10)]
    [InlineData(100, 100, 0, 50, 10)]
    [InlineData(100, 100, -5, 50, 10)]
    [InlineData(100, 100, 50, 0, 10)]
    [InlineData(100, 100, 50, -5, 10)]
    [InlineData(100, 100, 50, 50, -1)]
    public void Calculate_InvalidInputs_ThrowsArgumentOutOfRangeException(
        int workWidth, int workHeight, int prefWidth, int prefHeight, int margin)
    {
        var workArea = new Rectangle(0, 0, workWidth, workHeight);
        var preferredSize = new Size(prefWidth, prefHeight);

        Assert.Throws<ArgumentOutOfRangeException>(() => TrayPopupPlacement.Calculate(workArea, preferredSize, margin));
    }

    [Fact]
    public void Calculate_RepeatedCalls_ReturnsConsistentResults()
    {
        var workArea = new Rectangle(100, 200, 1024, 768);
        var preferredSize = new Size(400, 300);
        const int margin = 16;

        var first = TrayPopupPlacement.Calculate(workArea, preferredSize, margin);
        var second = TrayPopupPlacement.Calculate(workArea, preferredSize, margin);

        Assert.Equal(first, second);
    }
}

using System.Drawing;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class TrayIconArtworkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpdateBadgeStaysInTheUpperRightOnBothTaskbarThemes(bool lightTaskbar)
    {
        using var plain = TrayIconArtwork.Create(lightTaskbar);
        using var update = TrayIconArtwork.Create(lightTaskbar, updateAvailable: true);
        using var baseBitmap = plain.ToBitmap();
        using var updateBitmap = update.ToBitmap();
        Assert.Equal(baseBitmap.Size, updateBitmap.Size);

        var accent = MinimalDarkTheme.Accent.ToArgb();
        var badgePixels = from x in Enumerable.Range(0, updateBitmap.Width)
                          from y in Enumerable.Range(0, updateBitmap.Height)
                          where updateBitmap.GetPixel(x, y).ToArgb() == accent
                          select new Point(x, y);
        var points = badgePixels.ToArray();
        Assert.NotEmpty(points);
        Assert.All(points, point =>
        {
            Assert.True(point.X >= updateBitmap.Width / 2);
            Assert.True(point.Y < updateBitmap.Height / 2);
            Assert.NotEqual(accent, baseBitmap.GetPixel(point.X, point.Y).ToArgb());
        });
    }
}

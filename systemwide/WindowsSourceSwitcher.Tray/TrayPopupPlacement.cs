using System;
using System.Drawing;

namespace WindowsSourceSwitcher.Tray;

internal static class TrayPopupPlacement
{
    internal static Rectangle Calculate(Rectangle workArea, Size preferredSize, int margin = 12)
    {
        if (workArea.Width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea), workArea.Width, "Work area width must be positive.");
        }

        if (workArea.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea), workArea.Height, "Work area height must be positive.");
        }

        if (preferredSize.Width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(preferredSize), preferredSize.Width, "Preferred size width must be positive.");
        }

        if (preferredSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(preferredSize), preferredSize.Height, "Preferred size height must be positive.");
        }

        if (margin < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(margin), margin, "Margin cannot be negative.");
        }

        int minDimension = Math.Min(workArea.Width, workArea.Height);
        int maxMargin = (minDimension - 1) / 2;
        int effectiveMargin = Math.Min(margin, maxMargin);

        int availableWidth = workArea.Width - (2 * effectiveMargin);
        int availableHeight = workArea.Height - (2 * effectiveMargin);

        int width = Math.Min(preferredSize.Width, availableWidth);
        int height = Math.Min(preferredSize.Height, availableHeight);

        int x = workArea.Right - effectiveMargin - width;
        int y = workArea.Bottom - effectiveMargin - height;

        return new Rectangle(x, y, width, height);
    }
}

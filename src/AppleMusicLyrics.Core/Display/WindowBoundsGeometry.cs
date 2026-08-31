namespace AppleMusicLyrics.Core.Display;

/// <summary>
/// Pure physical-pixel geometry for centered HWND resize transitions.
/// </summary>
public static class WindowBoundsGeometry
{
    public static PixelRect ResizeAroundCenter(
        PixelRect currentRect,
        MonitorDescriptor monitor,
        double targetWidthDip,
        double targetHeightDip)
    {
        if (currentRect.Width <= 0 || currentRect.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentRect), "The current rectangle must have a positive size.");
        }

        var work = monitor.WorkAreaPx;
        var widthPx = Math.Clamp(
            (int)Math.Round(Math.Max(1, targetWidthDip) * monitor.ScaleX),
            1,
            Math.Max(1, work.Width));
        var heightPx = Math.Clamp(
            (int)Math.Round(Math.Max(1, targetHeightDip) * monitor.ScaleY),
            1,
            Math.Max(1, work.Height));
        var left = (int)Math.Round(currentRect.CenterX - widthPx / 2.0);
        var top = (int)Math.Round(currentRect.CenterY - heightPx / 2.0);
        left = Math.Clamp(left, work.Left, work.Right - widthPx);
        top = Math.Clamp(top, work.Top, work.Bottom - heightPx);

        return new PixelRect(left, top, left + widthPx, top + heightPx);
    }

    public static PixelRect Interpolate(PixelRect start, PixelRect target, double progress)
    {
        if (start.Width <= 0 || start.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "The start rectangle must have a positive size.");
        }

        if (target.Width <= 0 || target.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(target), "The target rectangle must have a positive size.");
        }

        var t = Math.Clamp(progress, 0.0, 1.0);
        if (t <= 0)
        {
            return start;
        }

        if (t >= 1)
        {
            return target;
        }

        var centerX = Lerp(start.CenterX, target.CenterX, t);
        var centerY = Lerp(start.CenterY, target.CenterY, t);
        var width = Math.Max(1, (int)Math.Round(Lerp(start.Width, target.Width, t)));
        var height = Math.Max(1, (int)Math.Round(Lerp(start.Height, target.Height, t)));
        var left = (int)Math.Round(centerX - width / 2.0);
        var top = (int)Math.Round(centerY - height / 2.0);
        return new PixelRect(left, top, left + width, top + height);
    }

    private static double Lerp(double start, double end, double progress)
    {
        return start + (end - start) * progress;
    }
}

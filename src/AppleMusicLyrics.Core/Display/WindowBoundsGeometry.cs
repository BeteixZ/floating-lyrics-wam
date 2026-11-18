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

        // Derive the new left/top by adjusting the current position for the change in size.
        // Using the integer half-delta (widthDiff / 2 with truncation toward zero) avoids the
        // rounding bias that occurs when re-deriving left from the half-pixel CenterX. The
        // truncation is symmetric: growing by 1 pixel leaves left unchanged (the extra pixel
        // goes to the right), and shrinking by 1 pixel also leaves left unchanged (the lost
        // pixel comes from the right). Over thousands of resize cycles this prevents the
        // accumulated sub-pixel drift that banker's rounding would otherwise produce.
        var widthDiff = widthPx - currentRect.Width;
        var heightDiff = heightPx - currentRect.Height;
        var left = currentRect.Left - widthDiff / 2;
        var top = currentRect.Top - heightDiff / 2;
        left = Math.Clamp(left, work.Left, work.Right - widthPx);
        top = Math.Clamp(top, work.Top, work.Bottom - heightPx);

        return new PixelRect(left, top, left + widthPx, top + heightPx);
    }

    /// <summary>
    /// A rect of the given DIP size centered on a fixed physical-pixel anchor, shifted (not
    /// resized) to stay inside the work area. Deriving every card from the same anchor keeps
    /// repeated layouts of the same size bit-identical, unlike re-centering on a rounded rect.
    /// </summary>
    public static PixelRect CenterAt(
        double centerX,
        double centerY,
        MonitorDescriptor monitor,
        double widthDip,
        double heightDip)
    {
        var work = monitor.WorkAreaPx;
        var widthPx = Math.Clamp(
            (int)Math.Round(Math.Max(1, widthDip) * monitor.ScaleX),
            1,
            Math.Max(1, work.Width));
        var heightPx = Math.Clamp(
            (int)Math.Round(Math.Max(1, heightDip) * monitor.ScaleY),
            1,
            Math.Max(1, work.Height));
        var left = (int)Math.Round(centerX - widthPx / 2.0);
        var top = (int)Math.Round(centerY - heightPx / 2.0);
        left = Math.Clamp(left, work.Left, work.Right - widthPx);
        top = Math.Clamp(top, work.Top, work.Bottom - heightPx);
        return new PixelRect(left, top, left + widthPx, top + heightPx);
    }

    /// <summary>
    /// A window rect that contains <paramref name="content"/> with generous padding, limited to
    /// the work area, so later content of a similar size fits without resizing the window.
    /// </summary>
    public static PixelRect Envelope(PixelRect content, MonitorDescriptor monitor, int padX, int padY)
    {
        var work = monitor.WorkAreaPx;
        var left = Math.Max(work.Left, Math.Min(content.Left, content.Left - padX));
        var top = Math.Max(work.Top, Math.Min(content.Top, content.Top - padY));
        var right = Math.Min(work.Right, Math.Max(content.Right, content.Right + padX));
        var bottom = Math.Min(work.Bottom, Math.Max(content.Bottom, content.Bottom + padY));
        // Content outside the work area (a rect clamped elsewhere) must still be contained.
        left = Math.Min(left, content.Left);
        top = Math.Min(top, content.Top);
        right = Math.Max(right, content.Right);
        bottom = Math.Max(bottom, content.Bottom);
        return new PixelRect(left, top, right, bottom);
    }

    public static bool Contains(PixelRect outer, PixelRect inner) => inner.Left >= outer.Left
        && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    public static PixelRect Union(PixelRect a, PixelRect b) => new(
        Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

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

        // Interpolate all four edges independently so the resulting rect converges
        // monotonically to the target without the rounding bias that re-deriving left
        // from an interpolated center + width would introduce.
        var left = (int)Math.Round(Lerp(start.Left, target.Left, t));
        var top = (int)Math.Round(Lerp(start.Top, target.Top, t));
        var right = (int)Math.Round(Lerp(start.Right, target.Right, t));
        var bottom = (int)Math.Round(Lerp(start.Bottom, target.Bottom, t));
        var width = Math.Max(1, right - left);
        var height = Math.Max(1, bottom - top);
        return new PixelRect(left, top, left + width, top + height);
    }

    private static double Lerp(double start, double end, double progress)
    {
        return start + (end - start) * progress;
    }
}

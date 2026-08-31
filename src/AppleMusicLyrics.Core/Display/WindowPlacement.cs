namespace AppleMusicLyrics.Core.Display;

public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);

    public int Height => Math.Max(0, Bottom - Top);

    public double CenterX => Left + Width / 2.0;

    public double CenterY => Top + Height / 2.0;

    public long IntersectionArea(PixelRect other)
    {
        var width = Math.Max(0, Math.Min(Right, other.Right) - Math.Max(Left, other.Left));
        var height = Math.Max(0, Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top));
        return (long)width * height;
    }
}

public sealed record MonitorDescriptor(
    string DeviceName,
    PixelRect BoundsPx,
    PixelRect WorkAreaPx,
    uint DpiX,
    uint DpiY,
    bool IsPrimary)
{
    public double ScaleX => Math.Max(96u, DpiX) / 96.0;

    public double ScaleY => Math.Max(96u, DpiY) / 96.0;
}

public sealed record ResolvedWindowPlacement(
    MonitorDescriptor Monitor,
    PixelRect WindowRectPx,
    double WidthDip,
    double HeightDip,
    double RelativeCenterX,
    double RelativeCenterY);

public sealed record CapturedWindowPlacement(
    string MonitorId,
    double RelativeCenterX,
    double RelativeCenterY);

/// <summary>
/// Pure placement math. Win32 boundaries stay in physical pixels while WPF sizes stay in DIPs.
/// </summary>
public static class WindowPlacementService
{
    public static ResolvedWindowPlacement Resolve(
        IReadOnlyList<MonitorDescriptor> monitors,
        string? preferredMonitorId,
        double relativeCenterX,
        double relativeCenterY,
        double widthDip,
        double heightDip,
        PixelRect legacyRect)
    {
        if (monitors.Count == 0)
        {
            throw new ArgumentException("At least one monitor is required.", nameof(monitors));
        }

        var target = FindTargetMonitor(monitors, preferredMonitorId, legacyRect);
        var work = target.WorkAreaPx;
        var widthPx = Math.Clamp((int)Math.Round(Math.Max(1, widthDip) * target.ScaleX), 1, Math.Max(1, work.Width));
        var heightPx = Math.Clamp((int)Math.Round(Math.Max(1, heightDip) * target.ScaleY), 1, Math.Max(1, work.Height));

        var anchorX = IsValidAnchor(relativeCenterX)
            ? relativeCenterX
            : work.Width > 0 ? (legacyRect.CenterX - work.Left) / work.Width : 0.5;
        var anchorY = IsValidAnchor(relativeCenterY)
            ? relativeCenterY
            : work.Height > 0 ? (legacyRect.CenterY - work.Top) / work.Height : 0.5;
        anchorX = Math.Clamp(anchorX, 0.0, 1.0);
        anchorY = Math.Clamp(anchorY, 0.0, 1.0);

        var centerX = work.Left + anchorX * work.Width;
        var centerY = work.Top + anchorY * work.Height;
        var left = (int)Math.Round(centerX - widthPx / 2.0);
        var top = (int)Math.Round(centerY - heightPx / 2.0);
        left = Math.Clamp(left, work.Left, work.Right - widthPx);
        top = Math.Clamp(top, work.Top, work.Bottom - heightPx);

        return new ResolvedWindowPlacement(
            target,
            new PixelRect(left, top, left + widthPx, top + heightPx),
            widthPx / target.ScaleX,
            heightPx / target.ScaleY,
            work.Width > 0 ? (left + widthPx / 2.0 - work.Left) / work.Width : 0.5,
            work.Height > 0 ? (top + heightPx / 2.0 - work.Top) / work.Height : 0.5);
    }

    public static ResolvedWindowPlacement ResolveDpiSuggestedRect(
        MonitorDescriptor monitor,
        PixelRect suggestedRect)
    {
        if (suggestedRect.Width <= 0 || suggestedRect.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(suggestedRect), "The suggested rectangle must have a positive size.");
        }

        var captured = Capture(monitor, suggestedRect);
        return new ResolvedWindowPlacement(
            monitor,
            suggestedRect,
            suggestedRect.Width / monitor.ScaleX,
            suggestedRect.Height / monitor.ScaleY,
            captured.RelativeCenterX,
            captured.RelativeCenterY);
    }

    public static CapturedWindowPlacement Capture(MonitorDescriptor monitor, PixelRect windowRectPx)
    {
        var work = monitor.WorkAreaPx;
        var relativeX = work.Width > 0 ? (windowRectPx.CenterX - work.Left) / work.Width : 0.5;
        var relativeY = work.Height > 0 ? (windowRectPx.CenterY - work.Top) / work.Height : 0.5;

        return new CapturedWindowPlacement(
            monitor.DeviceName,
            Math.Clamp(relativeX, 0.0, 1.0),
            Math.Clamp(relativeY, 0.0, 1.0));
    }

    public static MonitorDescriptor FindMonitorForRect(
        IReadOnlyList<MonitorDescriptor> monitors,
        PixelRect rect)
    {
        return monitors
            .OrderByDescending(monitor => monitor.BoundsPx.IntersectionArea(rect))
            .ThenByDescending(monitor => monitor.IsPrimary)
            .First();
    }

    private static MonitorDescriptor FindTargetMonitor(
        IReadOnlyList<MonitorDescriptor> monitors,
        string? preferredMonitorId,
        PixelRect legacyRect)
    {
        if (!string.IsNullOrWhiteSpace(preferredMonitorId))
        {
            return monitors.FirstOrDefault(monitor => string.Equals(
                    monitor.DeviceName,
                    preferredMonitorId,
                    StringComparison.OrdinalIgnoreCase))
                ?? monitors.FirstOrDefault(monitor => monitor.IsPrimary)
                ?? monitors[0];
        }

        var intersecting = monitors
            .Select(monitor => (Monitor: monitor, Area: monitor.BoundsPx.IntersectionArea(legacyRect)))
            .OrderByDescending(item => item.Area)
            .First();
        return intersecting.Area > 0
            ? intersecting.Monitor
            : monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors[0];
    }

    private static bool IsValidAnchor(double value)
    {
        return !double.IsNaN(value) && value is >= 0.0 and <= 1.0;
    }
}

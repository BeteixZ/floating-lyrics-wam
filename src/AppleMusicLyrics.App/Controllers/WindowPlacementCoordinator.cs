using AppleMusicLyrics.Core.Configuration;
using AppleMusicLyrics.Core.Display;
using AppleMusicLyrics.Infrastructure.Windows.Display;

namespace AppleMusicLyrics.App.Controllers;

public sealed class WindowPlacementCoordinator
{
    private readonly MonitorService _monitorService = new();

    public bool TryReadSuggestedRect(nint lParam, out PixelRect rect) =>
        _monitorService.TryReadSuggestedRect(lParam, out rect);

    public PixelRect GetWindowRect(nint hwnd) => _monitorService.GetWindowRect(hwnd);

    public IReadOnlyList<MonitorDescriptor> GetMonitors() => _monitorService.GetMonitors();

    public MonitorDescriptor? GetMonitorForWindow(nint hwnd, IReadOnlyList<MonitorDescriptor> monitors) =>
        _monitorService.GetMonitorForWindow(hwnd, monitors);

    public void SetWindowRect(nint hwnd, PixelRect rect) => _monitorService.SetWindowRect(hwnd, rect);

    public ResolvedWindowPlacement Resolve(
        AppSettings settings,
        bool pureMode,
        double desiredWidth,
        double desiredHeight,
        PixelRect fallbackRect)
    {
        return WindowPlacementService.Resolve(
            GetMonitors(),
            pureMode ? settings.PureModeMonitorId : settings.WindowMonitorId,
            pureMode ? settings.PureModeRelativeCenterX : settings.WindowRelativeCenterX,
            pureMode ? settings.PureModeRelativeCenterY : settings.WindowRelativeCenterY,
            desiredWidth,
            desiredHeight,
            fallbackRect);
    }

    public CapturedWindowPlacement? Capture(nint hwnd)
    {
        var monitors = GetMonitors();
        var rect = GetWindowRect(hwnd);
        if (monitors.Count == 0 || rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }

        var monitor = GetMonitorForWindow(hwnd, monitors)
            ?? WindowPlacementService.FindMonitorForRect(monitors, rect);
        return WindowPlacementService.Capture(monitor, rect);
    }

    public static void StoreAnchor(AppSettings settings, bool pureMode, ResolvedWindowPlacement placement)
    {
        if (pureMode)
        {
            settings.PureModeMonitorId = placement.Monitor.DeviceName;
            settings.PureModeRelativeCenterX = placement.RelativeCenterX;
            settings.PureModeRelativeCenterY = placement.RelativeCenterY;
        }
        else
        {
            settings.WindowMonitorId = placement.Monitor.DeviceName;
            settings.WindowRelativeCenterX = placement.RelativeCenterX;
            settings.WindowRelativeCenterY = placement.RelativeCenterY;
        }

        settings.WindowPlacementVersion = 1;
    }

    public static void StoreAnchor(AppSettings settings, bool pureMode, CapturedWindowPlacement placement)
    {
        if (pureMode)
        {
            settings.PureModeMonitorId = placement.MonitorId;
            settings.PureModeRelativeCenterX = placement.RelativeCenterX;
            settings.PureModeRelativeCenterY = placement.RelativeCenterY;
        }
        else
        {
            settings.WindowMonitorId = placement.MonitorId;
            settings.WindowRelativeCenterX = placement.RelativeCenterX;
            settings.WindowRelativeCenterY = placement.RelativeCenterY;
        }

        settings.WindowPlacementVersion = 1;
    }
}

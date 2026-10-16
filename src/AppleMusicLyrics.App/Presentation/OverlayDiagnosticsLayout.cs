using System.Windows;
using AppleMusicLyrics.Core.Configuration;
using AppleMusicLyrics.Core.Display;

namespace AppleMusicLyrics.App.Presentation;

public static class OverlayDiagnosticsLayout
{
    public static double ResolveOpacity(AppSettings settings, bool hasLyrics, bool isPlaying,
        bool isHovering, bool isWindowDragging)
        => OverlayOpacityResolver.Resolve(settings, hasLyrics, isPlaying,
            isHovering && (settings.PureMode || !settings.ShowDebugPanel), isWindowDragging);

    public static double GetStatusMaxWidth(double contentWidth)
        => Math.Min(220, Math.Max(80, contentWidth * 0.45));

    public static double MeasureHeight(FrameworkElement element, double width)
    {
        if (element.Visibility != Visibility.Visible) return 0;
        element.Measure(new System.Windows.Size(Math.Max(1, width), double.PositiveInfinity));
        return element.DesiredSize.Height;
    }

    public static double GetWindowHeight(double chromeAndHeaderHeight, double lyricHeight, double footerHeight,
        double minimumHeight, double maximumHeight)
    {
        var desired = chromeAndHeaderHeight + lyricHeight + footerHeight;
        return Math.Clamp(desired, minimumHeight, Math.Max(minimumHeight, maximumHeight));
    }
}

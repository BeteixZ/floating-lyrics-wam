using AppleMusicLyrics.Core.Configuration;

namespace AppleMusicLyrics.Core.Display;

/// <summary>
/// Resolves overlay opacity from persistent options and transient interaction state.
/// </summary>
public static class OverlayOpacityResolver
{
    public static double Resolve(
        AppSettings settings,
        bool hasLyrics,
        bool isPlaying,
        bool isHovering,
        bool isWindowDragging)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if ((settings.AutoHideNoLyrics && !hasLyrics)
            || (settings.FadeWhenPaused && !isPlaying))
        {
            return 0.0;
        }

        if (settings.PureMode && isWindowDragging)
        {
            return Math.Clamp(settings.PureModeDragOpacity, 0.2, 1.0);
        }

        if (isHovering && settings.HoverFadeEnabled)
        {
            return Math.Clamp(settings.HoverFadeMinOpacity, 0.0, 1.0);
        }

        return Math.Clamp(settings.OverlayOpacity, 0.2, 1.0);
    }
}

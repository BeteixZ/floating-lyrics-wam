namespace AppleMusicLyrics.Core.Display;

/// <summary>
/// Validates native window-message geometry without changing its physical-pixel coordinate space.
/// </summary>
public static class WindowMessageGeometry
{
    public static bool TryCreateSuggestedRect(
        int left,
        int top,
        int right,
        int bottom,
        out PixelRect rect)
    {
        rect = new PixelRect(left, top, right, bottom);
        return right > left && bottom > top;
    }
}

namespace AppleMusicLyrics.Core.Display;

/// <summary>
/// Decides how far a content-sized overlay may shrink, given what the next few lines will need.
/// Shrinking to fit the current line and growing back a moment later (a duet part ending just
/// before another begins) reads as the window flailing; shrinking when the following lines stay
/// small should happen at once.
/// </summary>
public static class LookaheadSizing
{
    /// <summary>
    /// Per dimension: grow to <paramref name="desired"/> at once; when shrinking, stop at what the
    /// upcoming lines need (never above what is shown now). Without a forecast, use desired.
    /// </summary>
    public static (double Width, double Height) Resolve(
        (double Width, double Height) desired,
        (double Width, double Height) shown,
        (double Width, double Height)? upcoming)
    {
        if (upcoming is not { } next)
        {
            return desired;
        }

        return (Hold(desired.Width, shown.Width, next.Width), Hold(desired.Height, shown.Height, next.Height));
    }

    private static double Hold(double desired, double shown, double upcoming)
    {
        if (!double.IsFinite(shown) || desired >= shown)
        {
            return desired;
        }

        return Math.Max(desired, Math.Min(shown, upcoming));
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;
using Size = System.Windows.Size;

namespace AppleMusicLyrics.App.Presentation;

/// <summary>Measures complete lyric text without a Window, visual tree, ellipsis or clipping.</summary>
public static class LyricFontSizeFitter
{
    public static double FindLargestFittingSize(string text, Typeface typeface,
        double maxSize, double minSize, double maxWidth, double maxHeight, bool singleLine,
        double pixelsPerDip, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(typeface);
        ValidatePositiveFinite(maxSize, nameof(maxSize));
        ValidatePositiveFinite(minSize, nameof(minSize));
        ValidatePositiveFinite(maxWidth, nameof(maxWidth));
        ValidatePositiveFinite(maxHeight, nameof(maxHeight));
        ValidatePositiveFinite(pixelsPerDip, nameof(pixelsPerDip));

        var start = Math.Max(maxSize, minSize);
        if (Fits(start))
        {
            return start;
        }

        var steps = (int)Math.Ceiling(start - minSize);
        if (steps <= 0)
        {
            return minSize;
        }

        // Fit is monotonic across the 1pt grid, so binary-search the smallest decrement that
        // fits instead of measuring every size from the top (up to ~6 measures instead of ~58).
        var low = 0;
        var high = steps;
        while (high - low > 1)
        {
            var mid = low + (high - low) / 2;
            if (Fits(Math.Max(minSize, start - mid)))
            {
                high = mid;
            }
            else
            {
                low = mid;
            }
        }

        // The configured minimum is the lower bound even if the available space cannot fit it.
        return Math.Max(minSize, start - high);

        bool Fits(double size)
        {
            var measured = Measure(text, typeface, size, maxWidth, singleLine, pixelsPerDip, culture);
            return measured.Width <= maxWidth && measured.Height <= maxHeight;
        }
    }

    public static Size Measure(string text, Typeface typeface, double fontSize,
        double maxWidth, bool singleLine, double pixelsPerDip, CultureInfo? culture = null)
    {
        var formattedText = new FormattedText(text, culture ?? CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, typeface, fontSize, Brushes.White, pixelsPerDip)
        {
            Trimming = TextTrimming.None,
        };
        // A single line must fit at its natural width. Multiline text may wrap, but its entire
        // height must be measured: MaxTextHeight/MaxLineCount would hide precisely the overflow
        // which the font-size decision needs to detect.
        if (!singleLine) formattedText.MaxTextWidth = maxWidth;
        return new Size(formattedText.WidthIncludingTrailingWhitespace, formattedText.Height);
    }

    private static void ValidatePositiveFinite(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(name);
    }
}

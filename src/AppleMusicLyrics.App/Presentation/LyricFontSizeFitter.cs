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

        for (var size = Math.Max(maxSize, minSize); size > minSize; size -= 1.0)
        {
            var measured = Measure(text, typeface, size, maxWidth, singleLine, pixelsPerDip, culture);
            if (measured.Width <= maxWidth && measured.Height <= maxHeight)
            {
                return size;
            }
        }

        // The configured minimum is the lower bound even if the available space cannot fit it.
        return minSize;
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

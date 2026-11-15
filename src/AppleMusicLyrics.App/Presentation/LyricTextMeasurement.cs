using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;

namespace AppleMusicLyrics.App.Presentation;

/// <summary>Shapes lyric text once so repeated width probes only re-wrap instead of re-shaping.</summary>
public sealed class LyricTextMeasurement
{
    private readonly FormattedText _formattedText;

    public LyricTextMeasurement(string? text, Typeface typeface, double fontSize, double lineHeight,
        double pixelsPerDip, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(typeface);
        var content = string.IsNullOrWhiteSpace(text) ? " " : text.Trim();
        _formattedText = new FormattedText(
            content,
            culture ?? CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.White,
            pixelsPerDip);
        if (lineHeight > 0)
        {
            _formattedText.LineHeight = lineHeight;
        }

        _formattedText.MaxLineCount = 1;
        _formattedText.Trimming = TextTrimming.CharacterEllipsis;
        NaturalWidth = Math.Ceiling(_formattedText.Width);
    }

    public double NaturalWidth { get; }

    public double MeasureHeight(double maxWidth, int maxLines)
    {
        _formattedText.MaxTextWidth = maxWidth;
        _formattedText.MaxLineCount = maxLines;
        _formattedText.Trimming = maxLines == 1 ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        return Math.Ceiling(_formattedText.Height);
    }
}

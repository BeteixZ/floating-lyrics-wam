using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using AppleMusicLyrics.App.Presentation;
using Xunit;

namespace AppleMusicLyrics.Tests.Display;

public sealed class LyricTextMeasurementTests
{
    [Theory]
    [InlineData("Short lyric")]
    [InlineData("A longer lyric sentence that wraps into several lines")]
    [InlineData("中英混合 lyrics 你好 world 🌍")]
    public void NaturalWidth_MatchesUnconstrainedSingleLineText(string lyric) => OnSta(() =>
    {
        var measurement = new LyricTextMeasurement(lyric, new Typeface("Arial"), 38, 0, 1, CultureInfo.InvariantCulture);
        Assert.Equal(FreshSize(lyric, 38, 0, null, 1).Width, measurement.NaturalWidth);
    });

    [Fact]
    public void MeasureHeight_MatchesFreshMeasurementsAcrossWidthsAndLineBudgets() => OnSta(() =>
    {
        const string lyric = "A very long lyric line with many words that keeps wrapping 中英混合你好世界";
        var measurement = new LyricTextMeasurement(lyric, new Typeface("Arial"), 38, 0, 1, CultureInfo.InvariantCulture);

        // Probe widths in both directions: re-wrapping a cached FormattedText must give the
        // same result as shaping a brand-new one, regardless of the previous probe width.
        foreach (var width in new[] { 640.0, 320, 200, 120, 200, 320, 640 })
        {
            foreach (var maxLines in new[] { 1, 2, 4 })
            {
                Assert.Equal(FreshSize(lyric, 38, 0, width, maxLines).Height,
                    measurement.MeasureHeight(width, maxLines));
            }
        }
    });

    [Fact]
    public void MeasureHeight_AppliesScaledLineHeight() => OnSta(() =>
    {
        const string lyric = "A longer lyric sentence that wraps into several lines";
        var measurement = new LyricTextMeasurement(lyric, new Typeface("Arial"), 38, 52, 1, CultureInfo.InvariantCulture);
        Assert.Equal(FreshSize(lyric, 38, 52, 240, 4).Height, measurement.MeasureHeight(240, 4));
    });

    [Fact]
    public void Constructor_WhitespaceTextMeasuresAsSingleSpace() => OnSta(() =>
    {
        var blank = new LyricTextMeasurement("   ", new Typeface("Arial"), 38, 0, 1, CultureInfo.InvariantCulture);
        var space = new LyricTextMeasurement(" ", new Typeface("Arial"), 38, 0, 1, CultureInfo.InvariantCulture);
        Assert.Equal(space.NaturalWidth, blank.NaturalWidth);
        Assert.Equal(space.MeasureHeight(200, 2), blank.MeasureHeight(200, 2));
    });

    [Fact]
    public void Constructor_TrimsSurroundingWhitespace() => OnSta(() =>
    {
        var padded = new LyricTextMeasurement("  lyric line  ", new Typeface("Arial"), 38, 0, 1, CultureInfo.InvariantCulture);
        var trimmed = new LyricTextMeasurement("lyric line", new Typeface("Arial"), 38, 0, 1, CultureInfo.InvariantCulture);
        Assert.Equal(trimmed.NaturalWidth, padded.NaturalWidth);
    });

    private static System.Windows.Size FreshSize(string text, double fontSize, double lineHeight,
        double? maxWidth, int maxLines)
    {
        var formatted = new FormattedText(
            text.Trim(),
            CultureInfo.InvariantCulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface("Arial"),
            fontSize,
            System.Windows.Media.Brushes.White,
            1);
        if (lineHeight > 0)
        {
            formatted.LineHeight = lineHeight;
        }

        if (maxWidth.HasValue)
        {
            formatted.MaxTextWidth = maxWidth.Value;
        }

        formatted.MaxLineCount = maxLines;
        formatted.Trimming = maxLines == 1 ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        return new System.Windows.Size(Math.Ceiling(formatted.Width), Math.Ceiling(formatted.Height));
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15)))
            throw new TimeoutException("Text measurement did not complete on its STA thread.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

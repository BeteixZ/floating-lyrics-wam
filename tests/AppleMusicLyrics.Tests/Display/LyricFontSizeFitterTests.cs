using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows.Media;
using AppleMusicLyrics.App.Presentation;
using Xunit;

namespace AppleMusicLyrics.Tests.Display;

public sealed class LyricFontSizeFitterTests
{
    [Fact]
    public void FindLargestFittingSize_KeepsMaximumWhenTheCompleteTextFits() => OnSta(() =>
    {
        var size = Fit("Short lyric", 38, 14, 1200, 100, singleLine: true);
        Assert.Equal(38, size);
    });

    [Fact]
    public void FindLargestFittingSize_SingleLineShrinksUsingItsNaturalUntrimmedWidth() => OnSta(() =>
    {
        const string lyric = "A longer lyric sentence";
        var typeface = new Typeface("Arial");
        var natural = LyricFontSizeFitter.Measure(lyric, typeface, 38, 240, true, 1, CultureInfo.InvariantCulture);
        Assert.True(natural.Width > 240);

        var size = Fit(lyric, 38, 14, 240, 80, singleLine: true);
        var measured = LyricFontSizeFitter.Measure(lyric, typeface, size, 240, true, 1, CultureInfo.InvariantCulture);

        Assert.InRange(size, 14, 37);
        Assert.True(measured.Width <= 240);
        Assert.True(measured.Height <= 80);
    });

    [Fact]
    public void FindLargestFittingSize_MultilineUsesFullHeightInsteadOfClippedTwoLines() => OnSta(() =>
    {
        const string lyric = "A longer lyric sentence that wraps into several lines";
        var typeface = new Typeface("Arial");
        var full = LyricFontSizeFitter.Measure(lyric, typeface, 38, 240, false, 1, CultureInfo.InvariantCulture);
        Assert.True(full.Height > 85);

        var size = Fit(lyric, 38, 14, 240, 85, singleLine: false);
        var measured = LyricFontSizeFitter.Measure(lyric, typeface, size, 240, false, 1, CultureInfo.InvariantCulture);

        Assert.InRange(size, 14, 37);
        Assert.True(measured.Width <= 240);
        Assert.True(measured.Height <= 85);
    });

    [Fact]
    public void Measure_DoesNotDiscardTextBeyondTheHeightOrLineBudget() => OnSta(() =>
    {
        var lyric = string.Join(' ', Enumerable.Repeat("A very long lyric line with many words", 15));
        var full = LyricFontSizeFitter.Measure(lyric, new Typeface("Arial"), 38, 240, false, 1);

        Assert.True(full.Height > 500);
        Assert.Equal(14, Fit(lyric, 38, 14, 240, 80, singleLine: false));
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void FindLargestFittingSize_MixedUnicodeFitsAtDifferentDpi(double pixelsPerDip) => OnSta(() =>
    {
        const string lyric = "中英混合 lyrics 你好 world 🌍";
        var typeface = new Typeface("Segoe UI");
        var size = LyricFontSizeFitter.FindLargestFittingSize(lyric, typeface, 38, 14,
            220, 70, false, pixelsPerDip, CultureInfo.InvariantCulture);
        var measured = LyricFontSizeFitter.Measure(lyric, typeface, size, 220, false,
            pixelsPerDip, CultureInfo.InvariantCulture);

        Assert.InRange(size, 14, 37);
        Assert.True(measured.Width <= 220);
        Assert.True(measured.Height <= 70);
    });

    private static double Fit(string lyric, double maximum, double minimum, double width, double height, bool singleLine) =>
        LyricFontSizeFitter.FindLargestFittingSize(lyric, new Typeface("Arial"), maximum, minimum,
            width, height, singleLine, 1, CultureInfo.InvariantCulture);

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
            throw new TimeoutException("Font measurement did not complete on its STA thread.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

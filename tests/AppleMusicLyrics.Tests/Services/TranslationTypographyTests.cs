using AppleMusicLyrics.App.Services;
using Xunit;

namespace AppleMusicLyrics.Tests.Services;

public sealed class TranslationTypographyTests
{
    [Theory]
    [InlineData(double.NaN, 20)]
    [InlineData(double.PositiveInfinity, 20)]
    [InlineData(double.NegativeInfinity, 20)]
    [InlineData(-10, 10)]
    [InlineData(0, 10)]
    [InlineData(9.9, 10)]
    [InlineData(10, 10)]
    [InlineData(20, 20)]
    [InlineData(27.5, 27.5)]
    [InlineData(48, 48)]
    [InlineData(80, 48)]
    public void GetFontSize_UsesStableIndependentSizeAndSanitizesInvalidSettings(double input, double expected)
    {
        Assert.Equal(expected, TranslationTypography.GetFontSize(input));
    }
}

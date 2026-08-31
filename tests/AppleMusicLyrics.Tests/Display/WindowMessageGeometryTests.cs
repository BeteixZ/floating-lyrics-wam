using AppleMusicLyrics.Core.Display;
using Xunit;

namespace AppleMusicLyrics.Tests.Display;

public sealed class WindowMessageGeometryTests
{
    [Fact]
    public void TryCreateSuggestedRect_PreservesNegativePhysicalCoordinates()
    {
        var valid = WindowMessageGeometry.TryCreateSuggestedRect(
            -2400,
            -200,
            -1200,
            700,
            out var rect);

        Assert.True(valid);
        Assert.Equal(new PixelRect(-2400, -200, -1200, 700), rect);
    }

    [Theory]
    [InlineData(100, 100, 100, 200)]
    [InlineData(100, 100, 200, 100)]
    [InlineData(200, 100, 100, 200)]
    public void TryCreateSuggestedRect_RejectsNonPositiveDimensions(
        int left,
        int top,
        int right,
        int bottom)
    {
        Assert.False(WindowMessageGeometry.TryCreateSuggestedRect(
            left,
            top,
            right,
            bottom,
            out _));
    }
}

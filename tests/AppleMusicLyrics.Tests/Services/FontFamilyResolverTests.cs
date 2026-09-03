using AppleMusicLyrics.App.Services;
using Xunit;

namespace AppleMusicLyrics.Tests.Services;

public sealed class FontFamilyResolverTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Optima")]
    [InlineData("optima")]
    public void Resolve_OptimaOrEmpty_ResolvesSuccessfully(string? fontName)
    {
        var fontFamily = FontFamilyResolver.Resolve(fontName);
        Assert.NotNull(fontFamily);
        Assert.Contains("Optima", fontFamily.Source);
    }

    [Fact]
    public void Resolve_CustomFont_ResolvesTargetFont()
    {
        var fontFamily = FontFamilyResolver.Resolve("Arial");
        Assert.NotNull(fontFamily);
        Assert.Equal("Arial", fontFamily.Source);
    }
}

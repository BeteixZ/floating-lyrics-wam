using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Parsing;
using Xunit;

namespace AppleMusicLyrics.Tests.Parsing;

public sealed class LyricsTranslationFilterTests
{
    [Theory]
    [InlineData("//")]
    [InlineData(" // ")]
    [InlineData("／／")]
    [InlineData("/")]
    [InlineData("...")]
    [InlineData("Lyrics By: Someone")]
    [InlineData("Lyrics by：某人")]
    [InlineData("lyrics by Someone")]
    [InlineData("Composed by: A")]
    [InlineData("Producer : B")]
    [InlineData("作词：某人")]
    [InlineData("作曲 : 某人")]
    [InlineData("译者：某人")]
    [InlineData("翻譯：某人")]
    [InlineData("[作词]：某人")]
    [InlineData("TME享有本翻译作品的著作权")]
    public void Clean_RemovesPlaceholdersCreditsAndCopyrightNotices(string translation)
    {
        Assert.Null(LyricsTranslationFilter.Clean(translation));
    }

    [Theory]
    [InlineData("我们属于彼此", "我们属于彼此")]
    [InlineData("  音乐让我们相遇  ", "音乐让我们相遇")]
    [InlineData("词不达意", "词不达意")]
    [InlineData("Music makes the people come together", "Music makes the people come together")]
    [InlineData("他/她离开了", "他/她离开了")]
    public void Clean_KeepsOrdinaryTranslations(string translation, string expected)
    {
        Assert.Equal(expected, LyricsTranslationFilter.Clean(translation));
    }

    [Fact]
    public void MergeLines_SkipsPlaceholderAndCreditRowsAndLetsLaterSourcesFillThem()
    {
        var merged = LyricsTranslationMerger.MergeLines(
            [new LyricsLine(0, 1, "作词 : Someone"), new LyricsLine(1, 2, "Hello"), new LyricsLine(3, 4, "Bye")],
            [new LyricsLine(0, 1, "Lyrics By: Someone"), new LyricsLine(1, 2, "//"), new LyricsLine(3, 4, "再见")]);
        Assert.Null(merged[0].Translation);
        Assert.Null(merged[1].Translation);
        Assert.Equal("再见", merged[2].Translation);

        var filled = LyricsTranslationMerger.MergeLines(
            [new LyricsLine(1, 2, "Hello", Translation: "//")],
            [new LyricsLine(1, 2, "你好")]);
        Assert.Equal("你好", filled[0].Translation);
    }
}

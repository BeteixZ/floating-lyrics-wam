using AppleMusicLyrics.App.Presentation;
using AppleMusicLyrics.Core.Models;
using Xunit;

namespace AppleMusicLyrics.Tests.Display;

public sealed class LyricRomanizationTests
{
    [Theory]
    [InlineData("離れられずにいたよ ずっと", "离れられずにいたよ ずっと")]
    [InlineData("見慣れてる景色があったから", "见惯れてる景色があったから")]
    public void Resolve_ReadsSimplifiedChineseCharactersAsTheirKanji(string kanji, string simplified)
    {
        var expected = Resolve(kanji);

        Assert.False(string.IsNullOrWhiteSpace(expected));
        Assert.Equal(expected, Resolve(simplified));
    }

    [Fact]
    public void Resolve_LeavesAnUnreadableNameUnromanized()
    {
        // 𠮷 has no dictionary reading and no simplified form to fall back on.
        Assert.Null(Resolve("𠮷野さんがいた"));
    }

    [Theory]
    [InlineData('离', '離')]
    [InlineData('见', '見')]
    [InlineData('惯', '慣')]
    [InlineData('说', '説')]
    [InlineData('恋', '恋')]
    [InlineData('会', '会')]
    public void ToKanji_MapsSimplifiedCharactersToJapaneseForms(char simplified, char kanji) =>
        Assert.Equal(kanji, KanjiVariants.ToKanji(simplified));

    private static string? Resolve(string text)
    {
        var line = new LyricsLine(0, 1, text);
        var document = new LyricsDocument(null, null, "test", DateTimeOffset.UnixEpoch, [line]);
        return LyricRomanization.Resolve(document, line, enabled: true);
    }
}

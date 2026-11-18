using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Parsing;
using Xunit;

namespace AppleMusicLyrics.Tests.Parsing;

public sealed class LyricsContentFilterTests
{
    [Fact]
    public void Clean_RemovesOriginalCreditRowsAndCleansTranslationsWithoutChangingSungTiming()
    {
        // Same layout as QQ Music's cached lyrics: English credits in Text, "//" in Translation,
        // and a model attribution attached to an ordinary original row.
        var first = new LyricsLine(10, 12, "Hello", Words: [new(10, 12, "Hello")], Translation: "你好",
            Romanization: "ni hao");
        var last = new LyricsLine(14, 17, "Goodbye", Translation: "以下歌词翻译由文曲大模型提供");
        var document = new LyricsDocument("QQ_1", "success", "qqmusic:1", DateTimeOffset.UtcNow,
            [new(0, 3, "Lyrics by：Someone", Translation: "//"),
                new(3, 6, "Composed by：Someone", Translation: "//"), first, last,
                new(17, 19, "翻译由文曲大模型生成"), new(19, 20, "TME享有本翻译作品的著作权")],
            DurationSeconds: 30, LeadingSilenceSeconds: 1, NativeOffsetSeconds: .25);

        var cleaned = LyricsContentFilter.Clean(document);

        Assert.Equal(document with { Lines = cleaned.Lines }, cleaned);
        Assert.Collection(cleaned.Lines,
            line => Assert.Same(first, line),
            line => Assert.Equal(last with { Translation = null }, line));
        Assert.Same(cleaned, LyricsContentFilter.Clean(document));
        Assert.Equal(6, document.Lines.Count);
        Assert.Equal("以下歌词翻译由文曲大模型提供", last.Translation);
    }

    [Fact]
    public void Clean_PreservesOrdinaryLyricsAndOriginalSeparatorsByReference()
    {
        var document = new LyricsDocument("AP_1", "success", "apple.json", DateTimeOffset.UtcNow,
            [new(0, 3, "Music makes the people come together", Translation: "音乐让我们相遇"),
                new(3, 5, "词不达意"), new(5, 7, "..."), new(7, 9, "Hello\nMusic brings us together")]);

        Assert.Same(document, LyricsContentFilter.Clean(document));
    }

    [Fact]
    public void Clean_CreditOnlyDocumentsHaveNoDisplayableLines()
    {
        var document = new LyricsDocument("id", "success", "memory", DateTimeOffset.UtcNow,
            [new(0, 3, "Lyrics by：Someone"), new(3, 6, "Composed by：Someone")]);

        Assert.Empty(LyricsContentFilter.Clean(document).Lines);
    }
}

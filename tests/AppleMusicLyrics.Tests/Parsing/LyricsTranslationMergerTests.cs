using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Parsing;
using Xunit;

namespace AppleMusicLyrics.Tests.Parsing;

public sealed class LyricsTranslationMergerTests
{
    [Fact]
    public void MergeLines_ReservesExactMatchesAndIgnoresSameTextPlaceholders()
    {
        var merged = LyricsTranslationMerger.MergeLines(
            [new LyricsLine(1, 1.4, "early"), new LyricsLine(1.4, 2, "Hello")],
            [new LyricsLine(1.4, 2, "Hello"), new LyricsLine(1.4, 2, "你好")]);
        Assert.Null(merged[0].Translation);
        Assert.Equal("你好", merged[1].Translation);
    }

    [Fact]
    public void MergeLines_FillsMissingTranslationsWithoutReplacingThePreferredSource()
    {
        var merged = LyricsTranslationMerger.MergeLines(
            [new LyricsLine(1, 2, "one", Translation: "首选"), new LyricsLine(3, 4, "two")],
            [new LyricsLine(1, 2, "备用"), new LyricsLine(3, 4, "填补")]);
        Assert.Equal("首选", merged[0].Translation);
        Assert.Equal("填补", merged[1].Translation);
    }

    [Fact]
    public void MergeLines_AlignsByNearestTimeAndPreservesOriginalWordTiming()
    {
        LyricWord[] words = [new(1, 1.5, "Hello"), new(1.5, 2, " world")];
        var original = new LyricsLine(1, 2, "Hello world", Words: words);
        var merged = LyricsTranslationMerger.MergeLines([original],
            [new LyricsLine(0.92, 2, "你好世界"), new LyricsLine(4, 5, "too far")]);
        Assert.Equal("你好世界", merged[0].Translation);
        Assert.Same(words, merged[0].Words);
        Assert.Null(original.Translation);
    }

    [Fact]
    public void MergeLines_DoesNotReuseTranslationsAcrossDifferentLines()
    {
        var merged = LyricsTranslationMerger.MergeLines(
            [new LyricsLine(1, 1.2, "one"), new LyricsLine(1.2, 2, "two"), new LyricsLine(8, 9, "far")],
            [new LyricsLine(1, 2, "译文")]);
        Assert.Equal("译文", merged[0].Translation);
        Assert.Null(merged[1].Translation);
        Assert.Null(merged[2].Translation);
    }

    [Fact]
    public void MergeLines_SuppressesSameTextAndRejectsInvalidTimes()
    {
        var merged = LyricsTranslationMerger.MergeLines(
            [new LyricsLine(1, 2, "Hello, world!"), new LyricsLine(3, 4, "second")],
            [new LyricsLine(1, 2, "HELLO WORLD"), new LyricsLine(double.NaN, 4, "bad")]);
        Assert.All(merged, line => Assert.Null(line.Translation));
    }
}

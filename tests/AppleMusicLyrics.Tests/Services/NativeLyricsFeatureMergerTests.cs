using AppleMusicLyrics.Application.Services;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Sync;
using Xunit;

namespace AppleMusicLyrics.Tests.Services;

public sealed class NativeLyricsFeatureMergerTests
{
    [Fact]
    public void Merge_PreservesNativeIdentityTextTimelineAndConvertsOffsetCoordinates()
    {
        var native = Document("AP_123", [new LyricsLine(10, 14, "Hello, WORLD!")], offset: 2);
        var source = Document("QQ_456", [new LyricsLine(11.5, 15.5, "hello world",
            Words: [new LyricWord(11.5, 13, "hello "), new LyricWord(13, 15.5, "world")], Translation: "你好，世界")], offset: .5);

        var merged = NativeLyricsFeatureMerger.MergeMissingFeatures(native, source, true, true);

        Assert.Equal(native with { Lines = merged.Lines }, merged);
        Assert.Equal((10d, 14d, "Hello, WORLD!"), (merged.Lines[0].Begin, merged.Lines[0].End, merged.Lines[0].Text));
        Assert.Equal("你好，世界", merged.Lines[0].Translation);
        Assert.Collection(merged.Lines[0].Words!,
            word => Assert.Equal(new LyricWord(10, 11.5, "Hello"), word),
            word => Assert.Equal(new LyricWord(11.5, 14, "WORLD"), word));
        Assert.True(LyricWordTiming.IsUsable(merged.Lines[0]));
        Assert.Null(native.Lines[0].Words);
        Assert.Null(native.Lines[0].Translation);
    }

    [Fact]
    public void Merge_NeverOverwritesNativeWordsOrTranslations()
    {
        LyricWord[] words = [new(0, 1, "hello"), new(1, 4, "world")];
        var native = Document("AP_123", [new LyricsLine(0, 4, "hello world", Words: words, Translation: "原生翻译")]);
        var source = Document("QQ", [new LyricsLine(0, 4, "hello world",
            Words: [new(0, 3, "hello "), new(3, 4, "world")], Translation: "外源翻译")]);

        Assert.Same(native, NativeLyricsFeatureMerger.MergeMissingFeatures(native, source, true, true));
        Assert.Same(words, native.Lines[0].Words);
    }

    [Theory]
    [InlineData("//", true)]
    [InlineData("Lyrics by: Someone", true)]
    [InlineData("hello world", false)]
    public void Merge_FillsPlaceholderButPreservesMeaningfulIdenticalNativeTranslation(string existing, bool replace)
    {
        var native = Document("AP_123", [new LyricsLine(0, 4, "hello world", Translation: existing)]);
        var source = Document("QQ", [new LyricsLine(0, 4, "hello world", Translation: "外部翻译")]);
        var merged = NativeLyricsFeatureMerger.MergeMissingFeatures(native, source, false, true);
        Assert.Equal(replace ? "外部翻译" : existing, merged.Lines[0].Translation);
        if (!replace) Assert.Same(native, merged);
    }

    [Fact]
    public void Merge_CanFillComplementaryFeaturesFromDifferentSourcesWithoutTradingAnyAway()
    {
        var native = Document("AP_123", [new LyricsLine(0, 4, "hello world")]);
        var words = Document("QQ", [new LyricsLine(0, 4, "hello world", Words: [new(0, 2, "hello"), new(2, 4, "world")])]);
        var translation = Document("NE", [new LyricsLine(0, 4, "hello world", Translation: "你好世界")]);
        var first = NativeLyricsFeatureMerger.MergeMissingFeatures(native, words, true, true);
        var complete = NativeLyricsFeatureMerger.MergeMissingFeatures(first, translation, true, true);

        Assert.Same(first.Lines[0].Words, complete.Lines[0].Words);
        Assert.Equal("你好世界", complete.Lines[0].Translation);
        Assert.Equal("AP_123", complete.LyricsId);
    }

    [Theory]
    [InlineData("different text", 0, 0)]
    [InlineData("hello world", 1, 0)]
    [InlineData("hello world", 0, 1)]
    public void Merge_RejectsTextOrEffectiveAudioWindowDisagreement(string text, double shift, double offset)
    {
        var native = Document("AP_123", [new LyricsLine(0, 4, "hello world")]);
        var source = Document("QQ", [new LyricsLine(shift, 4 + shift, text, Translation: "外源翻译")], offset);
        Assert.Same(native, NativeLyricsFeatureMerger.MergeMissingFeatures(native, source, false, true));
    }

    [Theory]
    [InlineData(.5, true)]
    [InlineData(.50001, false)]
    public void Merge_UsesHalfSecondAudioWindowTolerance(double shift, bool accepted)
    {
        var native = Document("AP_123", [new LyricsLine(0, 4, "hello world")]);
        var source = Document("QQ", [new LyricsLine(shift, 4 + shift, "hello world", Translation: "外源翻译")]);
        var merged = NativeLyricsFeatureMerger.MergeMissingFeatures(native, source, false, true);
        Assert.Equal(accepted, merged.Lines[0].Translation is not null);
    }

    [Fact]
    public void Merge_MapsRepeatedChorusesOneToOneByAudioTime()
    {
        var native = Document("AP_123", [new LyricsLine(0, 4, "again"), new LyricsLine(10, 14, "again")]);
        var source = Document("QQ", [new LyricsLine(0, 4, "again", Translation: "第一遍"), new LyricsLine(10, 14, "again", Translation: "第二遍")]);
        var merged = NativeLyricsFeatureMerger.MergeMissingFeatures(native, source, false, true);
        Assert.Equal("第一遍", merged.Lines[0].Translation);
        Assert.Equal("第二遍", merged.Lines[1].Translation);
    }

    [Fact]
    public void Merge_DoesNotReuseOneSourceLineForTwoNativeLines()
    {
        var native = Document("AP_123", [new LyricsLine(0, 1, "again"), new LyricsLine(.2, 1.2, "again")]);
        var source = Document("QQ", [new LyricsLine(.1, 1.1, "again", Translation: "只能补一行")]);
        var merged = NativeLyricsFeatureMerger.MergeMissingFeatures(native, source, false, true);
        Assert.NotNull(merged.Lines[0].Translation);
        Assert.Null(merged.Lines[1].Translation);
    }

    [Fact]
    public void Merge_DoesNotGuessBetweenAmbiguousRepeatedSourceLines()
    {
        var native = Document("AP_123", [new LyricsLine(0, 4, "again")]);
        var source = Document("QQ", [new LyricsLine(0, 4, "again", Translation: "甲"), new LyricsLine(0, 4, "again", Translation: "乙")]);
        Assert.Same(native, NativeLyricsFeatureMerger.MergeMissingFeatures(native, source, false, true));
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("outside")]
    [InlineData("backwards")]
    public void Merge_RejectsUnreliableImportedWordBindingAndTiming(string kind)
    {
        var native = Document("AP_123", [new LyricsLine(0, 4, "hello world")]);
        LyricWord[] words = kind switch
        {
            "omitted" => [new(0, 4, "hello")],
            "outside" => [new(0, 2, "hello"), new(4.2, 5, "world")],
            _ => [new(2, 4, "hello"), new(0, 2, "world")],
        };
        var source = Document("QQ", [new LyricsLine(0, 4, "hello world", Words: words)]);
        Assert.Same(native, NativeLyricsFeatureMerger.MergeMissingFeatures(native, source, true, false));
    }

    [Fact]
    public void Merge_CutsASyllableStillSoundingWhenTheNativeLineEnds()
    {
        var native = Document("AP_123", [new LyricsLine(0, 4, "hello world")]);
        var source = Document("QQ", [new LyricsLine(0, 5, "hello world",
            Words: [new(0, 2, "hello "), new(2, 5, "world")])]);
        var merged = NativeLyricsFeatureMerger.MergeMissingFeatures(native, source, true, false);
        Assert.Collection(merged.Lines[0].Words!,
            word => Assert.Equal(new LyricWord(0, 2, "hello"), word),
            word => Assert.Equal(new LyricWord(2, 4, "world"), word));
    }

    [Fact]
    public void Merge_AnchorsImportedSyllablesToTheNativeOnset()
    {
        var native = Document("AP_123", [new LyricsLine(10, 14, "hello world")]);
        var source = Document("QQ", [new LyricsLine(10.3, 14, "hello world",
            Words: [new(10.3, 12, "hello "), new(12, 13.8, "world")])]);
        var merged = NativeLyricsFeatureMerger.MergeMissingFeatures(native, source, true, false);
        Assert.Collection(merged.Lines[0].Words!,
            word => Assert.Equal((10d, 11.7d, "hello"), (word.Begin, Math.Round(word.End, 6), word.Text)),
            word => Assert.Equal((11.7d, 13.5d, "world"), (Math.Round(word.Begin, 6), Math.Round(word.End, 6), word.Text)));
    }

    [Fact]
    public void Merge_PairsByOnsetWhenTheSourceHoldsTheLineLonger()
    {
        // LRC sources end each line where the next one begins.
        var native = Document("AP_123", [new LyricsLine(0, 2.5, "hello world"), new LyricsLine(6, 8, "goodbye")]);
        var source = Document("NE", [new LyricsLine(.2, 6, "hello world", Translation: "你好世界"),
            new LyricsLine(6.1, 12, "goodbye", Translation: "再见")]);
        var merged = NativeLyricsFeatureMerger.MergeMissingFeatures(native, source, false, true);
        Assert.Equal(["你好世界", "再见"], merged.Lines.Select(line => line.Translation));
    }

    [Theory]
    [InlineData(1.2, true)]
    [InlineData(-2.5, true)]
    [InlineData(3.5, false)]
    public void Merge_ReregistersAConsistentlyShiftedSource(double shift, bool accepted)
    {
        string[] texts = ["one line", "two line", "three line", "four line"];
        var native = Document("AP_123", texts.Select((text, index) => new LyricsLine(10 * index + 5, 10 * index + 9, text)).ToArray());
        var source = Document("QQ", texts.Select((text, index) => new LyricsLine(10 * index + 5 + shift, 10 * index + 9 + shift, text,
            Words: [new(10 * index + 5 + shift, 10 * index + 9 + shift, text)], Translation: "译" + index)).ToArray());
        var merged = NativeLyricsFeatureMerger.MergeMissingFeatures(native, source, true, true);
        Assert.All(merged.Lines, line => Assert.Equal(accepted, line.Translation is not null));
        if (accepted)
            Assert.All(merged.Lines, line => Assert.Equal(line.Begin, line.Words![0].Begin, 6));
    }

    [Fact]
    public void Merge_DoesNotReregisterWithoutAConsensusShift()
    {
        string[] texts = ["one line", "two line", "three line", "four line"];
        double[] shifts = [0, 1.5, -1.5, 2.5];
        var native = Document("AP_123", texts.Select((text, index) => new LyricsLine(10 * index + 5, 10 * index + 9, text)).ToArray());
        var source = Document("QQ", texts.Select((text, index) => new LyricsLine(10 * index + 5 + shifts[index],
            10 * index + 9 + shifts[index], text, Translation: "译" + index)).ToArray());
        var merged = NativeLyricsFeatureMerger.MergeMissingFeatures(native, source, false, true);
        Assert.Equal(["译0", null, null, null], merged.Lines.Select(line => line.Translation));
    }

    [Fact]
    public void Merge_AcceptsZeroDurationFragmentAlongsideTimedBodyAndUntimedPunctuation()
    {
        var native = Document("AP_123", [new LyricsLine(0, 4, "hello, world!")]);
        var source = Document("QQ", [new LyricsLine(0, 4, "hello world",
            Words: [new(0, 0, "hello"), new(0, 4, "world")])]);
        var merged = NativeLyricsFeatureMerger.MergeMissingFeatures(native, source, true, false);
        Assert.True(LyricWordTiming.IsUsable(merged.Lines[0]));
        Assert.Equal(0, merged.Lines[0].Words![0].End);
    }

    [Fact]
    public void Merge_DoesNotReplaceEvenUnusableExistingNativeWordData()
    {
        var native = Document("AP_123", [new LyricsLine(0, 4, "hello world", Words: [new(0, 0, "hello")])]);
        var source = Document("QQ", [new LyricsLine(0, 4, "hello world", Words: [new(0, 4, "hello world")])]);
        Assert.Same(native, NativeLyricsFeatureMerger.MergeMissingFeatures(native, source, true, false));
    }

    [Fact]
    public void Merge_HonorsPreferencesAndRejectsPlainLyrics()
    {
        var native = Document("AP_123", [new LyricsLine(0, 4, "hello world")]);
        var source = Document("QQ", [new LyricsLine(0, 4, "hello world", Words: [new(0, 4, "hello world")], Translation: "你好世界")]);
        Assert.Same(native, NativeLyricsFeatureMerger.MergeMissingFeatures(native, source, false, false));
        Assert.Same(native, NativeLyricsFeatureMerger.MergeMissingFeatures(native, source with { Status = "plain" }, true, true));
    }

    private static LyricsDocument Document(string id, IReadOnlyList<LyricsLine> lines, double offset = 0) =>
        new(id, "success", id + ".json", DateTimeOffset.UnixEpoch, lines, 180, 1.5, offset);
}

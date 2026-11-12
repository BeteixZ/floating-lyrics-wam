using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Sync;
using Xunit;

namespace AppleMusicLyrics.Tests.Sync;

public sealed class LyricWordTimingTests
{
    [Theory]
    [InlineData("Hello world", true)]
    [InlineData("  Hello world  ", true)]
    [InlineData("Hello\t\nworld", true)]
    [InlineData("Hello\u3000world", true)]
    [InlineData("Hello, world!", true)]
    [InlineData("(Hello) — world...", true)]
    [InlineData("Hello\U00010100world", true)] // Supplementary-plane Unicode punctuation.
    [InlineData("Hello beautiful world", false)]
    [InlineData("Again Hello world", false)]
    [InlineData("Hello world again", false)]
    [InlineData("Hello World", false)]
    [InlineData("world Hello", false)]
    [InlineData("Hello + world", false)] // Omitted symbols are not punctuation separators.
    [InlineData("Hello 😀 world", false)]
    [InlineData("Hello\u200Bworld", false)] // An invisible format character is not whitespace.
    public void IsUsable_OnlyAllowsUntimedWhitespaceAndPunctuationGaps(string text, bool expected)
    {
        var line = Line(text, new LyricWord(10, 11, "Hello"), new LyricWord(11, 12, "world"));

        Assert.Equal(expected, LyricWordTiming.IsUsable(line));
    }

    [Theory]
    [InlineData("Hello world", " Hello", " world ")]
    [InlineData(" Hello world ", "Hello ", "world")]
    [InlineData("Hello  world", "Hello ", " world")]
    [InlineData("你好世界", "你好", "世界")]
    public void IsUsable_PreservesExistingExactConcatenationAdmission(string text, string first, string second)
    {
        Assert.True(LyricWordTiming.IsUsable(Line(text,
            new LyricWord(10, 11, first), new LyricWord(11, 12, second))));
    }

    [Fact]
    public void IsUsable_PreservesOuterWhitespaceOwnedBySeparateSourceFragments()
    {
        var line = Line("Hello", new LyricWord(9, 9, "  "),
            new LyricWord(10, 11, " Hello "), new LyricWord(11, 11, " "));

        Assert.True(LyricWordTiming.IsUsable(line));
    }

    [Fact]
    public void IsUsable_AllowsChinesePunctuationAndEmojiContent()
    {
        Assert.True(LyricWordTiming.IsUsable(Line("你好，世界！",
            new LyricWord(10, 11, "你好"), new LyricWord(11, 12, "世界"))));
        Assert.True(LyricWordTiming.IsUsable(Line("Hi 😀!",
            new LyricWord(10, 11, "Hi"), new LyricWord(11, 12, "😀"))));
    }

    [Fact]
    public void IsUsable_RejectsWrongOrRepeatedFragments()
    {
        Assert.False(LyricWordTiming.IsUsable(Line("Hello world",
            new LyricWord(10, 11, "Goodbye"), new LyricWord(11, 12, "world"))));
        Assert.False(LyricWordTiming.IsUsable(Line("Hello world",
            new LyricWord(10, 11, "Hello"), new LyricWord(11, 12, "Hello"), new LyricWord(12, 13, "world"))));
    }

    [Theory]
    [InlineData(double.NaN, 12)]
    [InlineData(10, double.NaN)]
    [InlineData(double.NegativeInfinity, 12)]
    [InlineData(10, double.PositiveInfinity)]
    [InlineData(-1, 12)]
    [InlineData(12, 10)]
    public void IsUsable_RejectsNonFiniteNegativeAndReversedWordIntervals(double begin, double end)
    {
        Assert.False(LyricWordTiming.IsUsable(Line("Hello", new LyricWord(begin, end, "Hello"))));
    }

    [Fact]
    public void IsUsable_RejectsMissingEmptyAndNullFragments()
    {
        Assert.False(LyricWordTiming.IsUsable(new LyricsLine(10, 14, "Hello")));
        Assert.False(LyricWordTiming.IsUsable(Line("Hello")));
        Assert.False(LyricWordTiming.IsUsable(Line("Hello", (LyricWord)null!)));
        Assert.False(LyricWordTiming.IsUsable(Line("Hello", new LyricWord(10, 11, string.Empty))));
        Assert.False(LyricWordTiming.IsUsable(Line("Hello", new LyricWord(10, 11, null!))));
        Assert.False(LyricWordTiming.IsUsable(Line(string.Empty, new LyricWord(10, 11, "Hello"))));
        Assert.False(LyricWordTiming.IsUsable(Line(" ", new LyricWord(10, 11, " "))));
        Assert.False(LyricWordTiming.IsUsable(new LyricsLine(10, 14, null!, Words: [new LyricWord(10, 11, "Hello")])));
        Assert.False(LyricWordTiming.IsUsable(null!));
    }

    [Fact]
    public void IsUsable_AllowsInstantPunctuationAlongsideTimedContent()
    {
        Assert.True(LyricWordTiming.IsUsable(Line("A!",
            new LyricWord(1, 2, "A"), new LyricWord(2, 2, "!"))));
        Assert.True(LyricWordTiming.IsUsable(Line("A B",
            new LyricWord(1, 2, "A"), new LyricWord(2, 2, " "), new LyricWord(2, 3, "B"))));
    }

    [Fact]
    public void IsUsable_DoesNotCountOnlyInstantOrOnlySeparatorTimingAsKaraokeContent()
    {
        Assert.False(LyricWordTiming.IsUsable(Line("A!",
            new LyricWord(1, 1, "A"), new LyricWord(1, 1, "!"))));
        Assert.False(LyricWordTiming.IsUsable(Line("A!",
            new LyricWord(1, 1, "A"), new LyricWord(1, 2, "!"))));
        Assert.False(LyricWordTiming.IsUsable(Line("A ",
            new LyricWord(1, 1, "A"), new LyricWord(1, 2, " "))));
        Assert.False(LyricWordTiming.IsUsable(Line("!", new LyricWord(1, 2, "!"))));
    }

    [Fact]
    public void IsUsable_DoesNotTightenExistingOverlappingOrSlightlyOutOfOrderQrcTimings()
    {
        var line = Line("One two three", new LyricWord(10, 13, "One "),
            new LyricWord(9.9, 11, "two "), new LyricWord(10.5, 12, "three"));

        Assert.True(LyricWordTiming.IsUsable(line));
    }

    [Fact]
    public void IsUsable_UsesTextOrderWithoutReinterpretingAbsoluteTimesAsLineOffsets()
    {
        var line = new LyricsLine(100, 104, "Hello world", Words:
            [new LyricWord(10, 11, "Hello"), new LyricWord(11, 12, "world")]);

        // This predicate only validates feature usability. It does not shift or clamp source
        // word times to a line window; cross-source alignment belongs to the merge policy.
        Assert.True(LyricWordTiming.IsUsable(line));
    }

    private static LyricsLine Line(string text, params LyricWord[] words) => new(10, 14, text, Words: words);
}

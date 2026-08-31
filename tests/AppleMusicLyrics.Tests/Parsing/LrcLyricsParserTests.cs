using AppleMusicLyrics.Core.Parsing;
using Xunit;

namespace AppleMusicLyrics.Tests.Parsing;

public sealed class LrcLyricsParserTests
{
    [Fact]
    public void ParseLines_ReadsTimestampsAndDerivesEndFromTheNextLine()
    {
        var lines = new LrcLyricsParser().ParseLines("""
        [00:12.34]first
        [00:15.50]second
        [01:02.00]third
        """);

        Assert.Equal(3, lines.Count);
        Assert.Equal(12.34, lines[0].Begin, 3);
        Assert.Equal(15.50, lines[0].End, 3);
        Assert.Equal("first", lines[0].Text);
        Assert.Equal(62.00, lines[2].Begin, 3);
    }

    [Theory]
    [InlineData("[00:05]x", 5.0)]
    [InlineData("[00:05.2]x", 5.2)]
    [InlineData("[00:05.25]x", 5.25)]
    [InlineData("[00:05.250]x", 5.25)]
    [InlineData("[00:05:25]x", 5.25)]
    public void ParseLines_AcceptsEveryFractionWidthSeenInTheWild(string text, double expectedBegin)
    {
        var lines = new LrcLyricsParser().ParseLines(text);

        Assert.Equal(expectedBegin, Assert.Single(lines).Begin, 3);
    }

    [Fact]
    public void ParseLines_ExpandsALineCarryingSeveralTimestamps()
    {
        var lines = new LrcLyricsParser().ParseLines("""
        [00:10.00][00:30.00]chorus
        [00:20.00]verse
        """);

        Assert.Equal(3, lines.Count);
        Assert.Equal(["chorus", "verse", "chorus"], lines.Select(line => line.Text));
        Assert.Equal(10.0, lines[0].Begin, 3);
        Assert.Equal(20.0, lines[1].Begin, 3);
        Assert.Equal(30.0, lines[2].Begin, 3);
    }

    [Fact]
    public void ParseLines_UsesABlankEntryToCloseThePrecedingLineWithoutShowingIt()
    {
        var lines = new LrcLyricsParser().ParseLines("""
        [00:10.00]sung line
        [00:14.00]
        [00:30.00]next sung line
        """);

        Assert.Equal(2, lines.Count);
        Assert.Equal("sung line", lines[0].Text);

        // The bare timestamp ends the line four seconds in rather than letting it run to 0:30.
        Assert.Equal(14.0, lines[0].End, 3);
        Assert.Equal(30.0, lines[1].Begin, 3);
    }

    [Fact]
    public void ParseLines_ShiftsEverythingWhenTheFileDeclaresAnOffset()
    {
        // "+" means show the lyrics earlier, so it comes off the timestamps.
        var lines = new LrcLyricsParser().ParseLines("""
        [offset:+500]
        [00:10.00]line
        """);

        Assert.Equal(9.5, Assert.Single(lines).Begin, 3);
    }

    [Fact]
    public void ParseLines_HandlesANegativeOffset()
    {
        var lines = new LrcLyricsParser().ParseLines("""
        [offset:-250]
        [00:10.00]line
        """);

        Assert.Equal(10.25, Assert.Single(lines).Begin, 3);
    }

    [Fact]
    public void ParseLines_IgnoresMetadataTagsAndUntimedText()
    {
        var lines = new LrcLyricsParser().ParseLines("""
        [ti:Some Title]
        [ar:Some Artist]
        [al:Some Album]
        [length:03:53]
        this line has no timestamp

        [00:10.00]the only real line
        """);

        Assert.Equal("the only real line", Assert.Single(lines).Text);
    }

    [Fact]
    public void ParseLines_SortsEntriesThatArriveOutOfOrder()
    {
        var lines = new LrcLyricsParser().ParseLines("""
        [00:30.00]third
        [00:10.00]first
        [00:20.00]second
        """);

        Assert.Equal(["first", "second", "third"], lines.Select(line => line.Text));
    }

    [Fact]
    public void ParseLines_EndsTheFinalLineAtTheTrackDurationWhenKnown()
    {
        var lines = new LrcLyricsParser().ParseLines("[00:10.00]last line", durationSeconds: 200.0);

        Assert.Equal(200.0, Assert.Single(lines).End, 3);
    }

    [Fact]
    public void ParseLines_GivesTheFinalLineATailWhenTheDurationIsUnusable()
    {
        // A duration that predates the last lyric cannot end it.
        var lines = new LrcLyricsParser().ParseLines("[01:00.00]last line", durationSeconds: 30.0);

        Assert.Equal(68.0, Assert.Single(lines).End, 3);
    }

    [Fact]
    public void ParseLines_ReturnsNothingForEmptyOrTimestampFreeInput()
    {
        var parser = new LrcLyricsParser();

        Assert.Empty(parser.ParseLines(string.Empty));
        Assert.Empty(parser.ParseLines("   "));
        Assert.Empty(parser.ParseLines("just some prose\nwith no timestamps"));
    }

    [Fact]
    public void Parse_ProducesADocumentCarryingTheSuppliedIdentity()
    {
        var document = new LrcLyricsParser().Parse(
            "[00:01.00]hello",
            sourceFile: "lrclib:42",
            lyricsId: "LRCLIB_42",
            durationSeconds: 180.0);

        Assert.Equal("LRCLIB_42", document.LyricsId);
        Assert.Equal("lrclib:42", document.SourceFile);
        Assert.Equal(180.0, document.DurationSeconds);
        Assert.Equal("success", document.Status);
        Assert.Single(document.Lines);
    }
}

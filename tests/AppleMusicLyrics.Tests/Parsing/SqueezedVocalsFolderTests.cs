using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Parsing;
using Xunit;

namespace AppleMusicLyrics.Tests.Parsing;

public sealed class SqueezedVocalsFolderTests
{
    // Timings from QQ Music's "Breakdown": backing vocals written as slivers between the lead lines.
    [Fact]
    public void Fold_TurnsSqueezedBackingVocalsIntoOneSmallRowUnderTheLeadLine()
    {
        var document = Document(
            Line(117.08, 120.20, "Well I guess I'm trying to be nonchalant about it"),
            Line(120.20, 120.52, "Break-breakdown", timed: true),
            Line(120.52, 123.43, "And I'm going to extremes to prove I'm fine"),
            Line(123.43, 124.08, "Steady breaking me on down", timed: true),
            Line(124.08, 127.38, "But in reality I'm slowly losing my mind"),
            Line(127.52, 127.99, "Losing my mind", timed: true),
            Line(127.99, 128.24, "Break-breakdown", timed: true),
            Line(128.24, 130.71, "Underneath the guise of a smile gradually I'm dying"));

        var folded = SqueezedVocalsFolder.Fold(document);

        Assert.Equal(4, folded.Lines.Count);
        Assert.All(folded.Lines, line => Assert.True(line.End - line.Begin >= 2.4));
        var first = folded.Lines[0];
        Assert.Equal(120.52, first.End);
        Assert.Equal("Break-breakdown", Assert.Single(first.BackgroundVocals!).Text);

        var merged = Assert.Single(folded.Lines[2].BackgroundVocals!);
        Assert.Equal("Losing my mind · Break-breakdown", merged.Text);
        Assert.Equal(127.52, merged.Begin);
        Assert.Equal(128.24, merged.End);
        // The source moved these fragments after the moment they are sung: no word clock is kept.
        Assert.Null(merged.Words);
        Assert.Null(merged.Translation);
        Assert.Null(Assert.Single(first.BackgroundVocals!).Words);
        Assert.Equal(128.24, folded.Lines[2].End);
        Assert.Same(folded, SqueezedVocalsFolder.Fold(document));
    }

    [Fact]
    public void Fold_AttachesLeadingSliversToTheLineTheyLeadInto()
    {
        var folded = SqueezedVocalsFolder.Fold(Document(
            Line(10.0, 14.0, "A long opening line that is sung slowly"),
            Line(15.0, 15.3, "Break-breakdown"),
            Line(15.3, 19.0, "And the next line is just as long as that")));

        Assert.Equal(2, folded.Lines.Count);
        Assert.Equal(15.0, folded.Lines[1].Begin);
        Assert.Equal("Break-breakdown", Assert.Single(folded.Lines[1].BackgroundVocals!).Text);
    }

    [Fact]
    public void Fold_KeepsShortButSingableLinesAndFastRap()
    {
        var document = Document(
            Line(10.0, 14.0, "A long opening line that is sung slowly"),
            Line(14.0, 14.36, "Yeah"),                                // 11 letters a second
            Line(14.4, 15.4, "Everyday crazy situations rocking"),     // a one-second rap line
            Line(15.4, 18.0, "Another line that is long enough"),
            Line(30.0, 30.3, "Break-breakdown"),                       // nothing adjacent to fold into
            Line(40.0, 44.0, "A final line long enough to hold it"));

        Assert.Same(document, SqueezedVocalsFolder.Fold(document));
    }

    [Fact]
    public void Fold_LeavesDocumentsThatAlreadyMarkBackgroundVocals()
    {
        var document = Document(
            Line(10.0, 14.0, "Apple marks its own backing vocals") with
            {
                BackgroundVocals = [Line(12.0, 13.0, "ooh")],
            },
            Line(14.0, 14.3, "Break-breakdown"),
            Line(14.3, 18.0, "And the next line is just as long"));

        Assert.Same(document, SqueezedVocalsFolder.Fold(document));
    }

    // Timings from Apple's "It's Like That": the 0.72 s reply is dense enough to look like a
    // sliver, but it is the other singer taking over and must keep its own line.
    [Fact]
    public void Fold_LeavesDocumentsThatTagTheirVocalists()
    {
        var document = Document(
            new LyricsLine(110.34, 114.02, "Don't stop, baby") { VocalistId = "v1" },
            new LyricsLine(113.79, 114.51, "Shake your ass") { VocalistId = "v2" },
            new LyricsLine(114.69, 118.36, "It's ecstasy") { VocalistId = "v1" });

        Assert.Same(document, SqueezedVocalsFolder.Fold(document));
        // The same timings without tags would be folded, so the tags are what keeps the reply.
        var untagged = document with { Lines = [.. document.Lines.Select(line => line with { VocalistId = null })] };
        Assert.Equal(2, SqueezedVocalsFolder.Fold(untagged).Lines.Count);
    }

    private static LyricsDocument Document(params LyricsLine[] lines) =>
        new("QQ_1", "success", "qqmusic:1", DateTimeOffset.UnixEpoch, lines, 283);

    private static LyricsLine Line(double begin, double end, string text, bool timed = false)
    {
        if (!timed) return new LyricsLine(begin, end, text, Translation: "译文");
        var parts = text.Split(' ');
        var step = (end - begin) / parts.Length;
        var words = parts.Select((part, index) => new LyricWord(begin + index * step, begin + (index + 1) * step,
            index < parts.Length - 1 ? part + " " : part)).ToArray();
        return new LyricsLine(begin, end, text, Words: words, Translation: "译文");
    }
}

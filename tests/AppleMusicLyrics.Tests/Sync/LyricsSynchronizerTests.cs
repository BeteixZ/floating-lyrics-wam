using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Sync;
using Xunit;

namespace AppleMusicLyrics.Tests.Sync;

public sealed class LyricsSynchronizerTests
{
    [Fact]
    public void Resolve_ReturnsCurrentLineInsideWindow()
    {
        var synchronizer = new LyricsSynchronizer();
        var document = new LyricsDocument(
            LyricsId: "1",
            Status: "ok",
            SourceFile: "sample.json",
            UpdatedAt: DateTimeOffset.UtcNow,
            Lines:
            [
                new LyricsLine(0, 1, "line one"),
                new LyricsLine(1, 2, "line two"),
            ]);
        var player = new PlayerState("Song", "Artist", null, 1.2, 180, true);

        var state = synchronizer.Resolve(document, player);

        Assert.Equal(1, state.CurrentIndex);
        Assert.Equal("line two", state.CurrentLine?.Text);
        Assert.Equal("line one", state.PreviousLine?.Text);
        Assert.Null(state.NextLine);
    }

    [Fact]
    public void Resolve_KeepsPreviousStartedLineCurrentBetweenTimedWindows()
    {
        var synchronizer = new LyricsSynchronizer();
        var document = new LyricsDocument(
            LyricsId: "1",
            Status: "ok",
            SourceFile: "sample.json",
            UpdatedAt: DateTimeOffset.UtcNow,
            Lines:
            [
                new LyricsLine(0, 1, "line one"),
                new LyricsLine(2, 3, "line two"),
                new LyricsLine(4, 5, "line three"),
            ]);
        var player = new PlayerState("Song", "Artist", null, 1.5, 180, true);

        var state = synchronizer.Resolve(document, player);

        Assert.Equal(0, state.CurrentIndex);
        Assert.Null(state.PreviousLine);
        Assert.Equal("line one", state.CurrentLine?.Text);
        Assert.Equal("line two", state.NextLine?.Text);
    }

    [Fact]
    public void Resolve_KeepsLastLineCurrentAfterLyricsEnd()
    {
        var synchronizer = new LyricsSynchronizer();
        var document = new LyricsDocument(
            LyricsId: "1",
            Status: "ok",
            SourceFile: "sample.json",
            UpdatedAt: DateTimeOffset.UtcNow,
            Lines:
            [
                new LyricsLine(0, 1, "line one"),
                new LyricsLine(1, 2, "line two"),
            ]);
        var player = new PlayerState("Song", "Artist", null, 4.0, 180, true);

        var state = synchronizer.Resolve(document, player);

        Assert.Equal(1, state.CurrentIndex);
        Assert.Equal("line one", state.PreviousLine?.Text);
        Assert.Equal("line two", state.CurrentLine?.Text);
        Assert.Null(state.NextLine);
    }
}

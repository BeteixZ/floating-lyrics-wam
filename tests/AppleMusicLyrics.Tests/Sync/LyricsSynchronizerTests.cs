using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Sync;
using Xunit;

namespace AppleMusicLyrics.Tests.Sync;

public sealed class LyricsSynchronizerTests
{
    [Fact]
    public void Resolve_ReusesStatesAndDoesNotWalkEarlierLinesOnSteadyFrames()
    {
        var lines = new CountingLines(Enumerable.Range(0, 10_000)
            .Select(index => new LyricsLine(index * 2, index * 2 + 1, "line " + index)).ToArray());
        var document = new LyricsDocument("long", "ok", "memory", DateTimeOffset.UtcNow, lines);
        var player = new PlayerState("Song", "Artist", null, 18_001.5, 20_000, true);
        var synchronizer = new LyricsSynchronizer();
        var initial = synchronizer.Resolve(document, player);
        Assert.Equal(9000, initial.CurrentIndex);
        lines.Reads = 0;
        Assert.Same(initial, synchronizer.Resolve(document, player));
        Assert.InRange(lines.Reads, 1, 8);
        Assert.Equal(2, synchronizer.Resolve(document, player with { Position = 4.5 }).CurrentIndex);
        // LyricsTiming clamps negative player positions to zero, where this timeline starts.
        Assert.Equal(0, synchronizer.Resolve(document, player with { Position = -1 }).CurrentIndex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_PreservesLinearSemanticsForGapsOverlapsAndSeeks(bool overlap)
    {
        var random = new Random(42);
        var lines = Enumerable.Range(0, 100).Select(index =>
            new LyricsLine(index * 2 + 1, index * 2 + (overlap ? 5 : 2), "line " + index)).ToArray();
        var document = new LyricsDocument("timeline", "ok", "memory", DateTimeOffset.UtcNow, lines);
        var player = new PlayerState("Song", "Artist", null, 0, 300, true);
        var synchronizer = new LyricsSynchronizer();
        for (var iteration = 0; iteration < 1000; iteration++)
        {
            var position = random.NextDouble() * 220;
            int? expected = null;
            if (position >= lines[0].Begin)
            {
                expected = lines.Length - 1;
                for (var index = 0; index < lines.Length; index++)
                {
                    if ((position >= lines[index].Begin && position < lines[index].End)
                        || (index + 1 < lines.Length && position >= lines[index].End && position < lines[index + 1].Begin))
                    { expected = index; break; }
                }
            }
            Assert.Equal(expected, synchronizer.Resolve(document, player with { Position = position }).CurrentIndex);
        }
    }

    [Fact]
    public void Resolve_PollAndFrameReadersCanShareTheIndexDuringConcurrentSeeks()
    {
        var lines = Enumerable.Range(0, 1000).Select(index => new LyricsLine(index * 2, index * 2 + 1, "line " + index)).ToArray();
        var document = new LyricsDocument("concurrent", "ok", "memory", DateTimeOffset.UtcNow, lines);
        var player = new PlayerState("Song", "Artist", null, 0, 2000, true);
        var synchronizer = new LyricsSynchronizer();
        Parallel.For(0, 4000, iteration =>
        {
            var index = (iteration * 397) % lines.Length;
            Assert.Equal(index, synchronizer.Resolve(document, player with { Position = index * 2 + .5 }).CurrentIndex);
        });
    }

    private sealed class CountingLines(LyricsLine[] values) : IReadOnlyList<LyricsLine>
    {
        public int Reads;
        public int Count => values.Length;
        public LyricsLine this[int index] { get { Reads++; return values[index]; } }
        public IEnumerator<LyricsLine> GetEnumerator() => ((IEnumerable<LyricsLine>)values).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

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
    public void Resolve_AppliesTheNativeLyricOffsetShippedInTheDocument()
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
            ],
            NativeOffsetSeconds: 0.512);
        var player = new PlayerState("Song", "Artist", null, 1.3, 180, true);

        // 1.3 lands in "line two" on the raw timeline, but the document says its own timings run
        // 0.512s ahead of this master, which puts playback back inside "line one".
        var state = synchronizer.Resolve(document, player);

        Assert.Equal(0, state.CurrentIndex);
        Assert.Equal("line one", state.CurrentLine?.Text);
    }

    [Fact]
    public void Resolve_IgnoresTheNativeLyricOffsetWhenDisabled()
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
            ],
            NativeOffsetSeconds: 0.512);
        var player = new PlayerState("Song", "Artist", null, 1.3, 180, true);

        var state = synchronizer.Resolve(document, player, offsetSeconds: 0.0, applyNativeOffset: false);

        Assert.Equal(1, state.CurrentIndex);
        Assert.Equal("line two", state.CurrentLine?.Text);
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

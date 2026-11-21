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
    public void Resolve_TracksTwoIndependentlyTimedDuetPartsAndResumesTheRemainingVoice()
    {
        var lead = new LyricsLine(10, 20, "lead") { VocalistId = "v1" };
        var firstReply = new LyricsLine(11, 14, "first reply") { VocalistId = "v2" };
        var secondReply = new LyricsLine(15, 21, "second reply") { VocalistId = "v2" };
        var next = new LyricsLine(22, 24, "next") { VocalistId = "v1" };
        var document = new LyricsDocument("id", "ok", "memory", DateTimeOffset.UtcNow, [lead, firstReply, secondReply, next]);
        var player = new PlayerState("Song", "Artist", null, 0, 24, true);
        var synchronizer = new LyricsSynchronizer();
        ActiveLyricState At(double position) => synchronizer.Resolve(document, player with { Position = position });

        Assert.Null(At(10).DuetLine);
        var firstPair = At(11);
        Assert.Same(lead, firstPair.CurrentLine);
        Assert.Same(firstReply, firstPair.DuetLine);
        Assert.Same(secondReply, firstPair.NextLine);
        Assert.Same(firstPair, At(13));
        Assert.Null(At(14).DuetLine);
        Assert.Same(secondReply, At(14).NextLine);
        Assert.Same(secondReply, At(15).DuetLine);
        Assert.Same(next, At(15).NextLine);
        Assert.Same(secondReply, At(20).CurrentLine);
        Assert.Null(At(20).DuetLine);
        Assert.Same(firstPair, At(11.5));
    }

    // Timings from Apple's "It's Like That": a reply coming in as the lead's last note fades
    // takes over, while a reply sung over the lead is shown alongside it.
    [Fact]
    public void Resolve_LetsTheOtherSingerTakeOverInsteadOfFlashingAPairAtEachExchange()
    {
        var lead = new LyricsLine(110.34, 114.02, "Don't stop, baby") { VocalistId = "v1" };
        var handoff = new LyricsLine(113.79, 114.51, "Shake your ass") { VocalistId = "v2" };
        var next = new LyricsLine(114.69, 118.36, "It's ecstasy") { VocalistId = "v1" };
        var verse = new LyricsLine(121.22, 124.24, "Tell me every little thing you wanna do") { VocalistId = "v1" };
        var reply = new LyricsLine(122.37, 124.24, "Do the thing, do the thing") { VocalistId = "v2" };
        var document = new LyricsDocument("id", "ok", "memory", DateTimeOffset.UtcNow, [lead, handoff, next, verse, reply]);
        var player = new PlayerState("Song", "Artist", null, 0, 240, true);
        var synchronizer = new LyricsSynchronizer();
        ActiveLyricState At(double position) => synchronizer.Resolve(document, player with { Position = position });

        Assert.Same(lead, At(113.5).CurrentLine);
        Assert.Null(At(113.5).DuetLine);
        var exchange = At(113.9);
        Assert.Same(handoff, exchange.CurrentLine);
        Assert.Null(exchange.DuetLine);
        Assert.Same(lead, exchange.PreviousLine);

        var together = At(123);
        Assert.Same(verse, together.CurrentLine);
        Assert.Same(reply, together.DuetLine);
    }

    [Theory]
    [InlineData("v1")]
    [InlineData(null)]
    public void Resolve_DoesNotPromoteAnOverlapWithoutADifferentTaggedVocalist(string? secondVocalist)
    {
        var lead = new LyricsLine(10, 20, "lead") { VocalistId = "v1" };
        var overlapping = new LyricsLine(11, 21, "overlapping") { VocalistId = secondVocalist };
        var document = new LyricsDocument("id", "ok", "memory", DateTimeOffset.UtcNow, [lead, overlapping]);
        var player = new PlayerState("Song", "Artist", null, 13, 30, true);
        var state = new LyricsSynchronizer().Resolve(document, player);

        Assert.Same(lead, state.CurrentLine);
        Assert.Null(state.DuetLine);
        Assert.Same(overlapping, state.NextLine);
    }

    // Apple's timings for "Everything is romantic": each line starts 0.1–0.35 s before the previous
    // one ends. Apple Music moves on at the new line's start; the overlay used to wait for the end.
    [Fact]
    public void Resolve_MovesToTheNextLineOfTheSameSingerAtItsStart()
    {
        LyricsLine[] lines =
        [
            new(102.578, 104.195, "Lemons on the trees and on the ground") { VocalistId = "v1" },
            new(104.106, 105.829, "Sandals on the stirrups of the scooters") { VocalistId = "v1" },
            new(105.650, 107.603, "Neon orange drinks on the beach") { VocalistId = "v1" },
            new(107.299, 109.038, "Four generations make up a family") { VocalistId = "v1" },
        ];
        var document = new LyricsDocument("id", "ok", "memory", DateTimeOffset.UtcNow, lines);
        var player = new PlayerState("Everything is romantic", "Charli xcx", "BRAT", 0, 200, true);
        var synchronizer = new LyricsSynchronizer();
        LyricsLine? At(double position) => synchronizer.Resolve(document, player with { Position = position }).CurrentLine;

        Assert.Same(lines[0], At(104.0));
        Assert.Same(lines[1], At(104.11));
        Assert.Same(lines[2], At(105.66));
        Assert.Same(lines[3], At(107.31));
        Assert.Same(lines[3], At(110));
    }

    // "Mean girls" pauses 13.8 s after "Think she already knows that you're obsessed"; Apple Music
    // shows its interlude dots there, while short pauses keep the finished line.
    [Theory]
    [InlineData(null)]
    [InlineData("v1")]
    public void Resolve_LongInstrumentalGapPreviewsTheNextLineInsteadOfHoldingTheLastOne(string? vocalist)
    {
        var obsessed = new LyricsLine(60.0, 63.2, "Think she already knows that you're obsessed") { VocalistId = vocalist };
        var chorus = new LyricsLine(77.0, 79.0, "This one's for all my mean girls") { VocalistId = vocalist };
        var shortPause = new LyricsLine(82.2, 84.0, "This one's for all my bad girls") { VocalistId = vocalist };
        var reply = new LyricsLine(82.5, 83.5, "reply") { VocalistId = vocalist is null ? null : "v2" };
        var document = new LyricsDocument("id", "ok", "memory", DateTimeOffset.UtcNow,
            vocalist is null ? [obsessed, chorus, shortPause] : [obsessed, chorus, shortPause, reply]);
        var player = new PlayerState("Mean girls", "Charli xcx", "BRAT", 0, 200, true);
        var synchronizer = new LyricsSynchronizer();
        ActiveLyricState At(double position) => synchronizer.Resolve(document, player with { Position = position });

        Assert.Same(obsessed, At(63.1).CurrentLine);
        var interlude = At(63.3);
        Assert.Null(interlude.CurrentLine);
        Assert.Same(obsessed, interlude.PreviousLine);
        Assert.Same(chorus, interlude.NextLine);
        Assert.Same(interlude, At(76.9));
        Assert.Same(chorus, At(77.0).CurrentLine);
        Assert.Same(chorus, At(80.5).CurrentLine);
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

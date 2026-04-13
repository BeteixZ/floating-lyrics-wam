using AppleMusicLyrics.App.Services;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Sync;
using Xunit;

namespace AppleMusicLyrics.Tests.Services;

public sealed class LyricsRuntimeServiceTests
{
    [Fact]
    public async Task SnapshotAsync_UsesPlaybackClockToAdvanceLyricSelection()
    {
        var document = new LyricsDocument(
            LyricsId: "runtime",
            Status: "ok",
            SourceFile: "sample.json",
            UpdatedAt: DateTimeOffset.UtcNow,
            Lines:
            [
                new LyricsLine(0.0, 0.05, "line one"),
                new LyricsLine(0.05, 0.50, "line two"),
            ]);

        var lyricsProvider = new StubLyricsProvider(document);
        var playerProvider = new StubPlayerProvider(new PlayerState("Song", "Artist", null, 0.0, 180, true));
        var runtime = new LyricsRuntimeService(
            lyricsProvider,
            playerProvider,
            new LyricsSynchronizer(),
            new PlaybackClock());

        _ = await runtime.SnapshotAsync();
        await Task.Delay(120);
        var snapshot = await runtime.SnapshotAsync();

        Assert.NotNull(snapshot.EstimatedPositionSeconds);
        Assert.NotNull(snapshot.RawPositionSeconds);
        Assert.True(snapshot.EstimatedPositionSeconds > snapshot.RawPositionSeconds);
        Assert.Equal("line two", snapshot.ActiveLyric.CurrentLine?.Text);
    }

    [Fact]
    public async Task SnapshotAsync_ClearsStaleDocumentAfterSessionChange()
    {
        var staleDocument = new LyricsDocument(
            LyricsId: "stale",
            Status: "ok",
            SourceFile: "stale.json",
            UpdatedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            Lines:
            [
                new LyricsLine(0.0, 5.0, "stale line"),
            ]);

        var lyricsProvider = new StubLyricsProvider(staleDocument);
        var playerProvider = new SequencePlayerProvider(
        [
            new PlayerState("Song A", "Artist", null, 0.0, 180, true),
            new PlayerState("Song B", "Artist", null, 0.0, 180, true),
        ]);

        var runtime = new LyricsRuntimeService(
            lyricsProvider,
            playerProvider,
            new LyricsSynchronizer(),
            new PlaybackClock());

        _ = await runtime.SnapshotAsync();
        var second = await runtime.SnapshotAsync();

        Assert.Null(second.Document);
        Assert.Null(second.ActiveLyric.CurrentLine);
    }

    [Fact]
    public async Task SnapshotAsync_UsesPlayerMatchedProviderForPreviouslyCachedSong()
    {
        var unrelatedLatest = new LyricsDocument(
            LyricsId: "latest",
            Status: "ok",
            SourceFile: "latest.json",
            UpdatedAt: DateTimeOffset.UtcNow.AddMinutes(-30),
            Lines:
            [
                new LyricsLine(0.0, 5.0, "some other song"),
            ],
            DurationSeconds: 210);

        var matchedDocument = new LyricsDocument(
            LyricsId: "rap-god",
            Status: "ok",
            SourceFile: "rap-god.json",
            UpdatedAt: DateTimeOffset.UtcNow.AddDays(-1),
            Lines:
            [
                new LyricsLine(25.0, 29.0, "I'm beginning to feel like a Rap God, Rap God"),
            ],
            DurationSeconds: 363.521);

        var lyricsProvider = new MatchingLyricsProvider(unrelatedLatest, matchedDocument);
        var playerProvider = new StubPlayerProvider(
            new PlayerState("Rap God", "Eminem", "The Marshall Mathers LP2", 26.0, 363.521, true));
        var runtime = new LyricsRuntimeService(
            lyricsProvider,
            playerProvider,
            new LyricsSynchronizer(),
            new PlaybackClock());

        var snapshot = await runtime.SnapshotAsync();

        Assert.NotNull(snapshot.Document);
        Assert.Equal("rap-god", snapshot.Document!.LyricsId);
        Assert.Equal("I'm beginning to feel like a Rap God, Rap God", snapshot.ActiveLyric.CurrentLine?.Text);
    }

    private sealed class StubLyricsProvider : ILyricsDocumentProvider
    {
        private readonly LyricsDocument _document;

        public StubLyricsProvider(LyricsDocument document)
        {
            _document = document;
        }

        public Task<LyricsDocument?> GetLatestLyricsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LyricsDocument?>(_document);
        }
    }

    private sealed class MatchingLyricsProvider : IPlayerMatchedLyricsProvider
    {
        private readonly LyricsDocument _latestDocument;
        private readonly LyricsDocument _matchedDocument;

        public MatchingLyricsProvider(LyricsDocument latestDocument, LyricsDocument matchedDocument)
        {
            _latestDocument = latestDocument;
            _matchedDocument = matchedDocument;
        }

        public Task<LyricsDocument?> GetLatestLyricsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LyricsDocument?>(_latestDocument);
        }

        public Task<LyricsDocument?> FindBestLyricsAsync(PlayerState player, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LyricsDocument?>(_matchedDocument);
        }
    }

    private sealed class StubPlayerProvider : IPlayerSessionProvider
    {
        private readonly PlayerState _playerState;

        public StubPlayerProvider(PlayerState playerState)
        {
            _playerState = playerState;
        }

        public Task<PlayerState?> GetCurrentPlayerStateAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<PlayerState?>(_playerState);
        }
    }

    private sealed class SequencePlayerProvider : IPlayerSessionProvider
    {
        private readonly Queue<PlayerState> _states;

        public SequencePlayerProvider(IEnumerable<PlayerState> states)
        {
            _states = new Queue<PlayerState>(states);
        }

        public Task<PlayerState?> GetCurrentPlayerStateAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<PlayerState?>(_states.Count > 1 ? _states.Dequeue() : _states.Peek());
        }
    }
}

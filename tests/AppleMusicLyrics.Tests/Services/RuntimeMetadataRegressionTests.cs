using System.Net;
using System.Text.Json;
using AppleMusicLyrics.Application.Services;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Sync;
using AppleMusicLyrics.Infrastructure.Windows.Catalog;
using Xunit;

namespace AppleMusicLyrics.Tests.Services;

public sealed class RuntimeMetadataRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurationCorrectionInvalidatesExternalHitOrMissWithoutAnyLocalSelection(bool initialMiss)
    {
        var player = new Player(new PlayerState("Song", "Artist", "Album", 1, 180, true));
        var provider = new External(state => Task.FromResult(initialMiss && state.Duration == 180
            ? null : Document("external_" + state.Duration, state.Duration)));
        await using var runtime = new LyricsRuntimeService(new Cache(), player, new LyricsSynchronizer())
        { ExternalLyricsProviders = [provider] };

        await runtime.SnapshotAsync();
        var first = await runtime.SnapshotAsync();
        Assert.Equal(initialMiss ? null : "external_180", first.Document?.LyricsId);
        player.State = player.State with { Duration = 200 };
        var changed = await runtime.SnapshotAsync();
        Assert.NotEqual("external_180", changed.Document?.LyricsId);
        var corrected = await runtime.SnapshotAsync();
        Assert.Equal("external_200", corrected.Document?.LyricsId);
        for (var poll = 0; poll < 5; poll++)
            Assert.Equal("external_200", (await runtime.SnapshotAsync()).Document?.LyricsId);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task DurationCorrectionDiscardsAnOldPendingExternalResult()
    {
        var player = new Player(new PlayerState("Song", "Artist", "Album", 1, 180, true));
        var oldResult = new TaskCompletionSource<LyricsDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new External(state => state.Duration == 180 ? oldResult.Task
            : Task.FromResult<LyricsDocument?>(Document("corrected", 200)));
        await using var runtime = new LyricsRuntimeService(new Cache(), player, new LyricsSynchronizer())
        { ExternalLyricsProviders = [provider] };

        await runtime.SnapshotAsync();
        player.State = player.State with { Duration = 200 };
        await runtime.SnapshotAsync();
        oldResult.SetResult(Document("stale", 180));
        Assert.Equal("corrected", (await runtime.SnapshotAsync()).Document?.LyricsId);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task DurationCorrectionRestartsCatalogAfterADefinitiveMiss()
    {
        var player = new Player(new PlayerState("Song", "Artist", "Album", 1, 180, true));
        var resolver = new RecordingCatalog();
        await using var runtime = new LyricsRuntimeService(new Cache(), player, new LyricsSynchronizer(), catalogResolver: resolver);
        await runtime.SnapshotAsync();
        await runtime.SnapshotAsync();
        player.State = player.State with { Duration = 200 };
        await runtime.SnapshotAsync();
        await runtime.SnapshotAsync();
        Assert.Equal(new[] { 180d, 200d }, resolver.Durations);
    }

    [Fact]
    public async Task ZeroDurationAndSmallJitterDoNotRestartCompletedExternalLookup()
    {
        var player = new Player(new PlayerState("Song", "Artist", "Album", 1, 180, true));
        var provider = new External(_ => Task.FromResult<LyricsDocument?>(Document("accepted", 180)));
        await using var runtime = new LyricsRuntimeService(new Cache(), player, new LyricsSynchronizer())
        { ExternalLyricsProviders = [provider] };
        await runtime.SnapshotAsync();
        await runtime.SnapshotAsync();
        foreach (var duration in new[] { 0, 180.2, 179.9, 180 })
        {
            player.State = player.State with { Duration = duration };
            Assert.Equal("accepted", (await runtime.SnapshotAsync()).Document?.LyricsId);
        }
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task LocalRegionalIdIsVerifiedEvenWhenSearchFindsAnotherRelease()
    {
        var local = Document("AP_222-zh-Hant", 64);
        var handler = new RegionalHandler();
        using var resolver = new ITunesCatalogSongResolver("us,cn", messageHandler: handler);
        var player = new Player(new PlayerState("Song", "Artist", "Album", 12, 180, true));
        await using var runtime = new LyricsRuntimeService(new Cache(local), player, new LyricsSynchronizer(),
            catalogResolver: resolver);

        var snapshot = await runtime.SnapshotAsync();
        Assert.Same(local, snapshot.Document);
        Assert.Equal(LyricsResolutionSource.CatalogVerifiedCache, snapshot.Resolution.Source);
        Assert.Equal(1, handler.Searches);
        Assert.Equal(2, handler.Lookups);
    }

    [Fact]
    public async Task VisualFramesStopExtrapolatingWhenTheTimelineStalls()
    {
        var time = new ManualTime();
        var player = new Player(new PlayerState("Song", "Artist", "Album", 10, 180, true));
        await using var runtime = new LyricsRuntimeService(new Cache(), player, new LyricsSynchronizer(),
            new PlaybackClock(time), timeProvider: time);
        var first = await runtime.SnapshotAsync();
        time.Advance(20);
        var stalledFrame = runtime.GetFrameSnapshot();
        Assert.InRange(stalledFrame.EstimatedPositionSeconds!.Value - first.EstimatedPositionSeconds!.Value, 0, 3);
        var stalledPoll = await runtime.SnapshotAsync();
        time.Advance(.2);
        Assert.Equal(stalledPoll.EstimatedPositionSeconds, runtime.GetFrameSnapshot().EstimatedPositionSeconds);
        player.State = player.State with { Position = 11 };
        var recovered = await runtime.SnapshotAsync();
        time.Advance(.2);
        Assert.True(runtime.GetFrameSnapshot().EstimatedPositionSeconds > recovered.EstimatedPositionSeconds);
    }

    private static LyricsDocument Document(string id, double duration) => new(id, "success", id,
        DateTimeOffset.UtcNow, [new LyricsLine(10, 20, "hello world")], DurationSeconds: duration);

    private sealed class RecordingCatalog : ICatalogSongResolver
    {
        public List<double> Durations = [];
        public Task<IReadOnlyList<string>> ResolveLyricsIdCandidatesAsync(PlayerState player, CancellationToken cancellationToken = default)
        { Durations.Add(player.Duration); return Task.FromResult<IReadOnlyList<string>>([]); }
    }

    private sealed class Player(PlayerState state) : IPlayerSessionProvider
    {
        public PlayerState State = state;
        public Task<PlayerState?> GetCurrentPlayerStateAsync(CancellationToken cancellationToken = default) => Task.FromResult<PlayerState?>(State);
    }

    private sealed class Cache(LyricsDocument? local = null) : IPlayerMatchedLyricsProvider
    {
        public Task<LyricsDocument?> GetLatestLyricsAsync(CancellationToken cancellationToken = default) => Task.FromResult(local);
        public Task<IReadOnlyList<LyricsMatch>> FindCandidatesAsync(PlayerState player, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LyricsMatch>>([]);
        public Task<IReadOnlyList<string>> GetCachedLyricsIdsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(local is null ? [] : [local.LyricsId!]);
        public Task<LyricsDocument?> FindByLyricsIdsAsync(IReadOnlyList<string> lyricsIds, CancellationToken cancellationToken = default) => Task.FromResult(lyricsIds.Contains("AP_222") ? local : null);
    }

    private sealed class External(Func<PlayerState, Task<LyricsDocument?>> fetch) : IExternalLyricsProvider
    {
        public string Name => "Regression";
        public int Calls;
        public Task<LyricsDocument?> FetchAsync(PlayerState player, CancellationToken cancellationToken = default)
        { Calls++; return fetch(player); }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(_ticks);
        public void Advance(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }

    private sealed class RegionalHandler : HttpMessageHandler
    {
        public int Searches, Lookups;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var search = request.RequestUri!.AbsolutePath == "/search";
            if (search) Searches++; else Lookups++;
            var id = search ? 111 : request.RequestUri.Query.Contains("country=cn", StringComparison.Ordinal) ? 222 : 0;
            var payload = id == 0 ? "{\"results\":[]}" : JsonSerializer.Serialize(new
            {
                results = new[] { new { trackId = id, trackName = "Song", artistName = "Artist", collectionName = "Album", trackTimeMillis = 180000 } },
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });
        }
    }
}

using System.Text.Json;
using AppleMusicLyrics.Application.Services;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Parsing;
using AppleMusicLyrics.Core.Sync;
using AppleMusicLyrics.Infrastructure.Windows.Cache;
using Xunit;

namespace AppleMusicLyrics.Tests.Services;

public sealed class ShortAppleLyricsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AppleMusicLyrics.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CatalogVerifiedLyricsSurviveEarlyTimelineEnd(bool hasOtherDurationCandidate, bool searchMiss)
    {
        WriteLyrics("short", "AP_1545337667-zh-Hant", "1:04.050");
        if (hasOtherDurationCandidate)
            WriteLyrics("other", "AP_999", "1:43.222");
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), [_root]);
        var player = new PlayerState("Welcome to Taichung City", "Flesh Juicer", "Golden Taizi Bro", 33, 103.222, true);
        var ordinary = await scanner.FindCandidatesAsync(player);
        Assert.DoesNotContain(ordinary, match => match.Document.LyricsId == "AP_1545337667-zh-Hant");
        var external = new CountingExternalProvider();
        await using var runtime = new LyricsRuntimeService(scanner, new FixedPlayer(player), new LyricsSynchronizer(),
            catalogResolver: new FixedCatalog(Task.FromResult<IReadOnlyList<string>>(["AP_1545337667"]), searchMiss))
        {
            ExternalLyricsProviders = [external],
        };

        var snapshot = await runtime.SnapshotAsync();

        Assert.Equal("AP_1545337667-zh-Hant", snapshot.Document?.LyricsId);
        Assert.Equal("歡迎來到台中", snapshot.ActiveLyric.CurrentLine?.Text);
        Assert.Equal(LyricsResolutionSource.CatalogVerifiedCache, snapshot.Resolution.Source);
        Assert.Equal(LyricsResolutionConfidence.High, snapshot.Resolution.Confidence);
        Assert.Contains(snapshot.Resolution.CandidateEvidence!, evidence => evidence.DurationDelta > 39);
        Assert.Equal(0, external.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("AP_999")]
    public async Task ShortTimelineIsNeverAcceptedWithoutMatchingCatalogId(string? catalogId)
    {
        WriteLyrics("short", "AP_1545337667", "1:04.050");
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), [_root]);
        await using var runtime = new LyricsRuntimeService(scanner,
            new FixedPlayer(new PlayerState("Welcome to Taichung City", "Flesh Juicer", null, 33, 103.222, true)),
            new LyricsSynchronizer(), catalogResolver: catalogId is null ? null : new FixedCatalog([catalogId]));

        Assert.Null((await runtime.SnapshotAsync()).Document);
    }

    [Fact]
    public async Task PendingCatalogDoesNotBlockPollingOrDisplayUnverifiedLyrics()
    {
        WriteLyrics("short", "AP_1545337667", "1:04.050");
        var catalog = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), [_root]);
        await using var runtime = new LyricsRuntimeService(scanner,
            new FixedPlayer(new PlayerState("Welcome to Taichung City", "Flesh Juicer", null, 33, 103.222, true)),
            new LyricsSynchronizer(), catalogResolver: new FixedCatalog(catalog.Task));

        var pending = await runtime.SnapshotAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(pending.Document);
        Assert.Equal(LyricsResolutionStatus.VerifyingCatalog, pending.Resolution.Status);
        catalog.SetResult(["AP_1545337667"]);
        var resolved = await runtime.SnapshotAsync();
        for (var attempt = 0; attempt < 100 && resolved.Document is null; attempt++)
        {
            await Task.Delay(10);
            resolved = await runtime.SnapshotAsync();
        }
        Assert.Equal("AP_1545337667", resolved.Document?.LyricsId);
    }

    [Fact]
    public async Task FailedCachedIdVerificationKeepsTheSuccessfulSearchResult()
    {
        // The only cached file matches by duration, but search names a different release.
        WriteLyrics("other", "AP_999", "1:43.222");
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), [_root]);
        var catalog = new FailingVerificationCatalog(["AP_1545337667"]);
        await using var runtime = new LyricsRuntimeService(scanner,
            new FixedPlayer(new PlayerState("Welcome to Taichung City", "Flesh Juicer", null, 33, 103.222, true)),
            new LyricsSynchronizer(), catalogResolver: catalog);

        var snapshot = await runtime.SnapshotAsync();
        for (var attempt = 0; attempt < 100 && snapshot.Resolution.Status == LyricsResolutionStatus.VerifyingCatalog; attempt++)
        {
            await Task.Delay(10);
            snapshot = await runtime.SnapshotAsync();
        }

        Assert.True(catalog.VerificationCalls > 0);
        // The search ids survive, so the duration-only file is positively rejected instead of
        // the whole catalog check being reported as a failure and retried.
        Assert.Equal(LyricsResolutionStatus.Unavailable, snapshot.Resolution.Status);
        Assert.Contains("lyric file is not cached", snapshot.Resolution.Summary);
        Assert.DoesNotContain("failed", snapshot.Resolution.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Null(snapshot.Document);
    }

    [Fact]
    public async Task CatalogLookupFindsLateCacheFileAndIgnoresEmptyDocument()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "ttmlLyrics-empty.json"), "{\"lyricsId\":\"AP_1545337667\",\"ttml\":\"<tt><body/></tt>\"}");
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), [_root]);
        Assert.Null(await scanner.FindByLyricsIdsAsync(["AP_1545337667"]));
        WriteLyrics("short", "AP_1545337667", "1:04.050");
        scanner.InvalidateFileIndex();

        Assert.Equal("AP_1545337667", (await scanner.FindByLyricsIdsAsync(["AP_1545337667"]))?.LyricsId);
        Assert.Null(await scanner.FindByLyricsIdsAsync(["AP_154533766"]));
    }

    private void WriteLyrics(string name, string id, string duration)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, $"ttmlLyrics-{name}.json"), JsonSerializer.Serialize(new
        {
            lyricsId = id,
            status = "success",
            ttml = $"<tt><body dur=\"{duration}\"><div><p begin=\"30\" end=\"40\">歡迎來到台中</p></div></body></tt>",
        }));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class FixedPlayer(PlayerState player) : IPlayerSessionProvider
    {
        public Task<PlayerState?> GetCurrentPlayerStateAsync(CancellationToken cancellationToken = default) => Task.FromResult<PlayerState?>(player);
    }

    private sealed class FixedCatalog(Task<IReadOnlyList<string>> result, bool searchMiss = false) : ICatalogSongResolver
    {
        public FixedCatalog(IReadOnlyList<string> ids) : this(Task.FromResult(ids)) { }
        public Task<IReadOnlyList<string>> ResolveLyricsIdCandidatesAsync(PlayerState player, CancellationToken cancellationToken = default) =>
            searchMiss ? Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()) : result;
        public Task<IReadOnlyList<string>> VerifyCachedLyricsIdsAsync(PlayerState player, IReadOnlyList<string> lyricsIds,
            CancellationToken cancellationToken = default)
        {
            Assert.Contains("AP_1545337667", lyricsIds);
            return result;
        }
    }

    private sealed class FailingVerificationCatalog(IReadOnlyList<string> searchIds) : ICatalogSongResolver
    {
        public int VerificationCalls { get; private set; }
        public Task<IReadOnlyList<string>> ResolveLyricsIdCandidatesAsync(PlayerState player, CancellationToken cancellationToken = default) =>
            Task.FromResult(searchIds);
        public Task<IReadOnlyList<string>> VerifyCachedLyricsIdsAsync(PlayerState player, IReadOnlyList<string> lyricsIds,
            CancellationToken cancellationToken = default)
        {
            VerificationCalls++;
            return Task.FromException<IReadOnlyList<string>>(new AggregateException(new System.Net.Http.HttpRequestException("rate limited")));
        }
    }

    private sealed class CountingExternalProvider : IExternalLyricsProvider
    {
        public string Name => "External";
        public int Calls { get; private set; }
        public Task<LyricsDocument?> FetchAsync(PlayerState player, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<LyricsDocument?>(null);
        }
    }
}

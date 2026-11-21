using System.Text.Json;
using AppleMusicLyrics.Application.Services;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Parsing;
using AppleMusicLyrics.Core.Sync;
using AppleMusicLyrics.Infrastructure.Windows.Cache;
using Xunit;

namespace AppleMusicLyrics.Tests.Services;

/// <summary>
/// Ariana Grande's "no tears left to cry" from "Sweetener (Bonus Videos Edition)": catalog search
/// names only the original album's ids, and Apple Music caches the reissue's own lyric file only
/// later, when its lyrics view opens. That file must replace the online lyrics shown meanwhile.
/// </summary>
public sealed class LateReissueLyricsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AppleMusicLyrics.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ALyricFileCachedLaterIsVerifiedEvenWhenSearchNamedAnotherRelease()
    {
        // Two unrelated cached songs of about the same length make duration alone inconclusive.
        WriteLyrics("other-a", "AP_300", "Some other song");
        WriteLyrics("other-b", "AP_400", "Yet another song");
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), [_root]);
        var player = new PlayerState("no tears left to cry", "Ariana Grande", "Sweetener (Bonus Videos Edition)", 63, 206, true);
        var catalog = new ReleaseCatalog(searchIds: ["AP_1399204228", "AP_1399202959"], reissueId: "AP_1544324124");
        var online = new LyricsDocument("QQ_1", "success", "qqmusic:1", DateTimeOffset.UtcNow,
            [new LyricsLine(1, 5, "no tears left to cry")], 206);
        await using var runtime = new LyricsRuntimeService(scanner, new FixedPlayer(player), new LyricsSynchronizer(),
            catalogResolver: catalog)
        {
            ExternalLyricsProviders = [new FixedExternal(online)],
            IdleRescanInterval = TimeSpan.FromMilliseconds(1),
            MaxIdleRescanInterval = TimeSpan.FromMilliseconds(1),
            CatalogRetryBaseDelay = TimeSpan.Zero,
            ExternalRetryBaseDelay = TimeSpan.Zero,
        };

        var before = await WaitAsync(runtime, snapshot => snapshot.Document?.LyricsId == "QQ_1");
        Assert.Equal(LyricsResolutionSource.ExternalProvider, before.Resolution.Source);

        WriteLyrics("reissue", "AP_1544324124", "no tears left to cry");
        var after = await WaitAsync(runtime, snapshot => snapshot.Document?.LyricsId == "AP_1544324124");

        Assert.Equal(LyricsResolutionSource.CatalogVerifiedCache, after.Resolution.Source);
        // The new file was checked once; the files the first lookup already covered were not re-sent.
        Assert.Equal(1, catalog.VerificationsOf("AP_1544324124"));
        for (var poll = 0; poll < 20; poll++) await runtime.SnapshotAsync();
        Assert.Equal(1, catalog.VerificationsOf("AP_1544324124"));
    }

    // Charli xcx's "360": a one-word title gives no content evidence, and the catalog was unreachable.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithoutTheCatalogAFileWrittenForThisTrackIsUsedButAPrefetchIsNot(bool writtenAfterStart)
    {
        var player = new PlayerState("360", "Charli xcx", "BRAT", 30, 134, true);
        if (!writtenAfterStart)
        {
            // Prefetched while the previous song played: written before this track started.
            WriteLyrics("360", "AP_1739079976", "Yeah, 360", "2:13.805");
            File.SetLastWriteTimeUtc(Path.Combine(_root, "ttmlLyrics-360.json"), DateTime.UtcNow.AddMinutes(-2));
        }
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser(), [_root]);
        await using var runtime = new LyricsRuntimeService(scanner, new FixedPlayer(player), new LyricsSynchronizer(),
            catalogResolver: new UnreachableCatalog())
        {
            IdleRescanInterval = TimeSpan.FromMilliseconds(1),
            MaxIdleRescanInterval = TimeSpan.FromMilliseconds(1),
            CatalogRetryBaseDelay = TimeSpan.Zero,
        };

        await runtime.SnapshotAsync();
        if (writtenAfterStart) WriteLyrics("360", "AP_1739079976", "Yeah, 360", "2:13.805");

        if (writtenAfterStart)
        {
            var snapshot = await WaitAsync(runtime, snapshot => snapshot.Document is not null);
            Assert.Equal("AP_1739079976", snapshot.Document!.LyricsId);
            Assert.Equal(LyricsResolutionConfidence.Medium, snapshot.Resolution.Confidence);
        }
        else
        {
            for (var poll = 0; poll < 60; poll++)
            {
                Assert.Null((await runtime.SnapshotAsync()).Document);
                await Task.Delay(5);
            }
        }
    }

    private static async Task<RuntimeSnapshot> WaitAsync(LyricsRuntimeService runtime, Func<RuntimeSnapshot, bool> predicate)
    {
        for (var poll = 0; poll < 300; poll++)
        {
            var snapshot = await runtime.SnapshotAsync();
            if (predicate(snapshot)) return snapshot;
            await Task.Delay(10);
        }
        throw new TimeoutException("The expected lyrics were not shown.");
    }

    private void WriteLyrics(string name, string id, string text, string duration = "3:25.900")
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, $"ttmlLyrics-{name}.json"), JsonSerializer.Serialize(new
        {
            lyricsId = id,
            status = "success",
            ttml = $"<tt><body dur=\"{duration}\"><div><p begin=\"1\" end=\"5\">{text}</p></div></body></tt>",
        }));
    }

    private sealed class UnreachableCatalog : ICatalogSongResolver
    {
        public Task<IReadOnlyList<string>> ResolveLyricsIdCandidatesAsync(PlayerState player,
            CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<string>>(new System.Net.Http.HttpRequestException("timed out"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FixedPlayer(PlayerState player) : IPlayerSessionProvider
    {
        public Task<PlayerState?> GetCurrentPlayerStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<PlayerState?>(player);
    }

    // Search names the original album; the lookup endpoint confirms the reissue's id as this track.
    private sealed class ReleaseCatalog(IReadOnlyList<string> searchIds, string reissueId) : ICatalogSongResolver
    {
        private readonly List<string> _verified = [];

        public int VerificationsOf(string id)
        {
            lock (_verified) return _verified.Count(item => item == id);
        }

        public Task<IReadOnlyList<string>> ResolveLyricsIdCandidatesAsync(PlayerState player,
            CancellationToken cancellationToken = default) => Task.FromResult(searchIds);

        public Task<IReadOnlyList<string>> VerifyCachedLyricsIdsAsync(PlayerState player, IReadOnlyList<string> lyricsIds,
            CancellationToken cancellationToken = default)
        {
            lock (_verified) _verified.AddRange(lyricsIds);
            return Task.FromResult<IReadOnlyList<string>>(lyricsIds.Contains(reissueId) ? [reissueId] : []);
        }
    }

    private sealed class FixedExternal(LyricsDocument document) : IExternalLyricsProvider
    {
        public string Name => "QQ Music";
        public Task<LyricsDocument?> FetchAsync(PlayerState player, CancellationToken cancellationToken = default) =>
            Task.FromResult<LyricsDocument?>(document);
    }
}

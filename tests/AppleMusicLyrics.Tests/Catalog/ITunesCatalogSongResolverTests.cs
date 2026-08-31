using System.Net;
using System.Net.Http;
using AppleMusicLyrics.Core.Matching;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Infrastructure.Windows.Catalog;
using Xunit;

namespace AppleMusicLyrics.Tests.Catalog;

public sealed class ITunesCatalogSongResolverTests
{
    [Fact]
    public void CatalogLyricsId_RoundTripsTheCatalogSongId()
    {
        Assert.Equal("AP_1892545101", CatalogLyricsId.FromCatalogId(1892545101));
        Assert.Equal(1892545101, CatalogLyricsId.ToCatalogId("AP_1892545101"));
        Assert.Null(CatalogLyricsId.ToCatalogId("1892545101"));
        Assert.Null(CatalogLyricsId.ToCatalogId("AP_not-a-number"));
        Assert.Null(CatalogLyricsId.ToCatalogId(null));
    }

    [Fact]
    public async Task ResolveLyricsIdCandidatesAsync_RanksTheMatchingReleaseFirst()
    {
        // Shaped after a real response: the same recording appears several times under different
        // catalog ids, which is exactly why duration alone cannot pick the right cache file.
        var handler = new StubHandler("""
        {
          "resultCount": 3,
          "results": [
            {"trackId": 6784831924, "trackName": "I Feel So Free", "artistName": "Madonna",
             "collectionName": "I Feel So Free - Single", "trackTimeMillis": 303530},
            {"trackId": 1892545101, "trackName": "I Feel So Free", "artistName": "Madonna",
             "collectionName": "CONFESSIONS II", "trackTimeMillis": 299480},
            {"trackId": 6767687986, "trackName": "I Feel So Free (Peggy Gou Energy Mix)",
             "artistName": "Madonna & Peggy Gou",
             "collectionName": "I Feel So Free (Peggy Gou Energy Mix) - Single", "trackTimeMillis": 267510}
          ]
        }
        """);

        using var resolver = new ITunesCatalogSongResolver("us", cachePath: null, messageHandler: handler);
        var player = new PlayerState("I Feel So Free", "Madonna", "CONFESSIONS II", 10.0, 299.48, true);

        var candidates = await resolver.ResolveLyricsIdCandidatesAsync(player);

        Assert.Equal("AP_1892545101", candidates[0]);
        Assert.Contains("AP_6784831924", candidates);
    }

    [Fact]
    public async Task ResolveLyricsIdCandidatesAsync_StopsAtTheFirstStorefrontThatMatches()
    {
        var handler = new StubHandler("""
        {"resultCount": 1, "results": [
          {"trackId": 1, "trackName": "Song", "artistName": "Artist",
           "collectionName": "Album", "trackTimeMillis": 180000}
        ]}
        """);

        using var resolver = new ITunesCatalogSongResolver("us,cn,jp,gb", cachePath: null, messageHandler: handler);
        var player = new PlayerState("Song", "Artist", "Album", 0.0, 180.0, true);

        var candidates = await resolver.ResolveLyricsIdCandidatesAsync(player);

        Assert.Equal(["AP_1"], candidates);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ResolveLyricsIdCandidatesAsync_CachesResultsSoARepeatIsOffline()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), "AppleMusicLyrics.Tests", Guid.NewGuid().ToString("N"), "catalog.json");
        var handler = new StubHandler("""
        {"resultCount": 1, "results": [
          {"trackId": 42, "trackName": "Song", "artistName": "Artist",
           "collectionName": "Album", "trackTimeMillis": 180000}
        ]}
        """);

        var player = new PlayerState("Song", "Artist", "Album", 0.0, 180.0, true);

        try
        {
            using (var resolver = new ITunesCatalogSongResolver("us", cachePath, messageHandler: handler))
            {
                Assert.Equal(["AP_42"], await resolver.ResolveLyricsIdCandidatesAsync(player));
                Assert.Equal(["AP_42"], await resolver.ResolveLyricsIdCandidatesAsync(player));
                Assert.Equal(1, handler.RequestCount);
            }

            // A fresh resolver reads the persisted answer instead of going back to the network.
            var offline = new StubHandler(statusCode: HttpStatusCode.ServiceUnavailable);
            using (var resolver = new ITunesCatalogSongResolver("us", cachePath, messageHandler: offline))
            {
                Assert.Equal(["AP_42"], await resolver.ResolveLyricsIdCandidatesAsync(player));
                Assert.Equal(0, offline.RequestCount);
            }
        }
        finally
        {
            var directory = Path.GetDirectoryName(cachePath);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ResolveLyricsIdCandidatesAsync_ThrowsWhenTheLookupFails()
    {
        var handler = new StubHandler(statusCode: HttpStatusCode.ServiceUnavailable);

        using var resolver = new ITunesCatalogSongResolver("us", cachePath: null, messageHandler: handler);
        var player = new PlayerState("Song", "Artist", "Album", 0.0, 180.0, true);

        await Assert.ThrowsAsync<HttpRequestException>(() => resolver.ResolveLyricsIdCandidatesAsync(player));
    }

    [Fact]
    public async Task ResolveLyricsIdCandidatesAsync_ThrowsWhenTheNetworkThrows()
    {
        var handler = new ThrowingHandler();

        using var resolver = new ITunesCatalogSongResolver("us", cachePath: null, messageHandler: handler);
        var player = new PlayerState("Song", "Artist", "Album", 0.0, 180.0, true);

        await Assert.ThrowsAsync<HttpRequestException>(() => resolver.ResolveLyricsIdCandidatesAsync(player));
    }

    [Fact]
    public async Task ResolveLyricsIdCandidatesAsync_IgnoresResultsWithADifferentTitle()
    {
        var handler = new StubHandler("""
        {"resultCount": 1, "results": [
          {"trackId": 7, "trackName": "A Completely Different Song", "artistName": "Artist",
           "collectionName": "Album", "trackTimeMillis": 180000}
        ]}
        """);

        using var resolver = new ITunesCatalogSongResolver("us", cachePath: null, messageHandler: handler);
        var player = new PlayerState("Song", "Artist", "Album", 0.0, 180.0, true);

        Assert.Empty(await resolver.ResolveLyricsIdCandidatesAsync(player));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _payload;
        private readonly HttpStatusCode _statusCode;

        public StubHandler(string payload = "{\"resultCount\":0,\"results\":[]}", HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _payload = payload;
            _statusCode = statusCode;
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_payload),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("offline");
        }
    }
}

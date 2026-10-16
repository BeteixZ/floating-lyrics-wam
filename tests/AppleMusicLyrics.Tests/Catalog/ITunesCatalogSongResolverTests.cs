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
        Assert.Equal(6808333042, CatalogLyricsId.ToCatalogId("AP_6808333042-en"));
        Assert.Equal(6808333042, CatalogLyricsId.ToCatalogId("AP_6808333042-zh-Hant"));
        Assert.True(CatalogLyricsId.Matches("AP_6808333042-en", "AP_6808333042"));
        Assert.False(CatalogLyricsId.Matches("AP_6808333042-en", "AP_6808333048"));
        Assert.Null(CatalogLyricsId.ToCatalogId("1892545101"));
        Assert.Null(CatalogLyricsId.ToCatalogId("AP_not-a-number"));
        Assert.Null(CatalogLyricsId.ToCatalogId("AP_6808333042-"));
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

    [Fact]
    public async Task ResolveLyricsIdCandidatesAsync_RejectsTheFullSongForAnIntro()
    {
        var handler = new StubHandler("""
        {"resultCount": 1, "results": [
          {"trackId": 1, "trackName": "Family Tree", "artistName": "Ethel Cain",
           "collectionName": "Preacher’s Daughter", "trackTimeMillis": 430000}
        ]}
        """);
        using var resolver = new ITunesCatalogSongResolver("us", cachePath: null, messageHandler: handler);
        var player = new PlayerState("Family Tree (Intro)", "Ethel Cain", "Preacher’s Daughter", 0, 221, true);

        Assert.Empty(await resolver.ResolveLyricsIdCandidatesAsync(player));
    }

    [Fact]
    public async Task ResolveLyricsIdCandidatesAsync_RejectsADifferentArtistEvenWhenTitleAndDurationMatch()
    {
        var handler = new StubHandler("""
        {"resultCount": 1, "results": [
          {"trackId": 7, "trackName": "Song", "artistName": "Someone Else",
           "collectionName": "Album", "trackTimeMillis": 180000}
        ]}
        """);
        using var resolver = new ITunesCatalogSongResolver("us", cachePath: null, messageHandler: handler);
        var player = new PlayerState("Song", "Artist", "Album", 0, 180, true);

        Assert.Empty(await resolver.ResolveLyricsIdCandidatesAsync(player));
    }

    [Fact]
    public async Task ResolveLyricsIdCandidatesAsync_ContinuesStorefrontsAfterAnExactTitleWithTheWrongDuration()
    {
        var handler = new StorefrontHandler(request => request.RequestUri!.Query.Contains("country=us", StringComparison.Ordinal)
            ? """
              {"resultCount": 1, "results": [
                {"trackId": 1, "trackName": "Family Tree (Intro)", "artistName": "Ethel Cain",
                 "collectionName": "Preacher’s Daughter", "trackTimeMillis": 430000}
              ]}
              """
            : """
              {"resultCount": 1, "results": [
                {"trackId": 2, "trackName": "Family Tree (Intro)", "artistName": "Ethel Cain",
                 "collectionName": "Preacher’s Daughter", "trackTimeMillis": 221000}
              ]}
              """);
        using var resolver = new ITunesCatalogSongResolver("us,cn,jp", cachePath: null, messageHandler: handler);
        var player = new PlayerState("Family Tree (Intro)", "Ethel Cain", "Preacher’s Daughter", 0, 221, true);

        Assert.Equal(["AP_2"], await resolver.ResolveLyricsIdCandidatesAsync(player));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task ResolveLyricsIdCandidatesAsync_IgnoresLegacyCachedIdsAndPersistsTheValidatedKey()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AppleMusicLyrics.Tests", Guid.NewGuid().ToString("N"));
        var cachePath = Path.Combine(directory, "catalog.json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(cachePath, """{"familytreeintro|ethelcain|preachersdaughter|221":["AP_1"]}""");
        var handler = new StubHandler("""
        {"resultCount": 1, "results": [
          {"trackId": 2, "trackName": "Family Tree (Intro)", "artistName": "Ethel Cain",
           "collectionName": "Preacher’s Daughter", "trackTimeMillis": 221000}
        ]}
        """);
        var player = new PlayerState("Family Tree (Intro)", "Ethel Cain", "Preacher’s Daughter", 0, 221, true);
        try
        {
            using (var resolver = new ITunesCatalogSongResolver("us", cachePath, messageHandler: handler))
            {
                Assert.Equal(["AP_2"], await resolver.ResolveLyricsIdCandidatesAsync(player));
                Assert.Equal(1, handler.RequestCount);
            }
            using (var cache = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cachePath)))
            {
                // Preserve the old entry without trusting it. No user cache file is deleted.
                Assert.Equal("AP_1", cache.RootElement.GetProperty("familytreeintro|ethelcain|preachersdaughter|221")[0].GetString());
                Assert.Equal("AP_2", cache.RootElement.GetProperty("v2|familytreeintro|ethelcain|preachersdaughter|221")[0].GetString());
            }
            var offline = new StubHandler(statusCode: HttpStatusCode.ServiceUnavailable);
            using (var resolver = new ITunesCatalogSongResolver("us", cachePath, messageHandler: offline))
            {
                Assert.Equal(["AP_2"], await resolver.ResolveLyricsIdCandidatesAsync(player));
                Assert.Equal(0, offline.RequestCount);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class StorefrontHandler(Func<HttpRequestMessage, string> payload) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload(request)),
            });
        }
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

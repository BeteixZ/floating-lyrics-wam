using System.Net;
using System.Net.Http;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Infrastructure.Windows.External;
using Xunit;

namespace AppleMusicLyrics.Tests.External;

public sealed class LrcLibLyricsProviderTests
{
    private static readonly PlayerState Player = new("S.E.X.", "Madonna", "Veronica Electronica", 3.0, 251.49, true);

    [Fact]
    public async Task FetchAsync_UsesTheExactEndpointFirst()
    {
        var handler = new RoutedHandler
        {
            OnGet = Json("""
            {"id": 4242, "trackName": "S.E.X.", "artistName": "Madonna",
             "albumName": "Veronica Electronica", "duration": 251.0, "instrumental": false,
             "plainLyrics": "one\ntwo",
             "syncedLyrics": "[00:10.00]one\n[00:20.00]two"}
            """),
        };

        using var provider = new LrcLibLyricsProvider(messageHandler: handler);

        var document = await provider.FetchAsync(Player);

        Assert.NotNull(document);
        Assert.Equal("LRCLIB_4242", document!.LyricsId);
        Assert.Equal(2, document.Lines.Count);
        Assert.Equal(10.0, document.Lines[0].Begin, 3);
        Assert.Equal(1, handler.GetCount);
        Assert.Equal(0, handler.SearchCount);
    }

    [Fact]
    public async Task FetchAsync_FallsBackToSearchWhenTheExactLookupMisses()
    {
        var handler = new RoutedHandler
        {
            OnGet = new HttpResponseMessage(HttpStatusCode.NotFound),
            OnSearch = Json("""
            [
              {"id": 1, "trackName": "S.E.X. (Live)", "artistName": "Madonna",
               "albumName": "Live", "duration": 300.0, "syncedLyrics": "[00:01.00]live version"},
              {"id": 2, "trackName": "S.E.X.", "artistName": "Madonna",
               "albumName": "Veronica Electronica", "duration": 251.0,
               "syncedLyrics": "[00:05.00]the right one"}
            ]
            """),
        };

        using var provider = new LrcLibLyricsProvider(messageHandler: handler);

        var document = await provider.FetchAsync(Player);

        Assert.Equal("LRCLIB_2", document!.LyricsId);
        Assert.Equal("the right one", document.Lines[0].Text);
        Assert.Equal(1, handler.SearchCount);
    }

    [Fact]
    public async Task FetchAsync_PrefersTheClosestDurationOverAnExactAlbumTitle()
    {
        // Rows and durations taken verbatim from a live lrclib search for Madonna / "S.E.X.".
        // 19940257 is within 0.12s of the playing track but its album carries an edition suffix,
        // while 22719150 spells the album exactly and is 2.6s out. Length wins: an edition suffix
        // is cosmetic, a 2.6s difference is a different master with different timings.
        var handler = new RoutedHandler
        {
            OnGet = new HttpResponseMessage(HttpStatusCode.NotFound),
            OnSearch = Json("""
            [
              {"id": 36841363, "trackName": "S.E.X.", "artistName": "Madonna",
               "albumName": "Baldur's Gate 3 (Original Game Soundtrack)", "duration": 233.0,
               "syncedLyrics": "[00:01.00]wrong"},
              {"id": 22719150, "trackName": "S.E.X.", "artistName": "Madonna",
               "albumName": "Rebel Heart", "duration": 254.124,
               "syncedLyrics": "[00:01.00]exact album"},
              {"id": 19940257, "trackName": "S.E.X.", "artistName": "Madonna",
               "albumName": "Rebel Heart (Limited Special Edition)", "duration": 251.611429,
               "syncedLyrics": "[00:01.00]closest duration"},
              {"id": 18919418, "trackName": "S.E.X.", "artistName": "Madonna",
               "albumName": "Rebel Heart", "duration": 224.112,
               "syncedLyrics": "[00:01.00]too short"}
            ]
            """),
        };

        using var provider = new LrcLibLyricsProvider(messageHandler: handler);
        var player = new PlayerState("S.E.X.", "Madonna", "Rebel Heart", 3.0, 251.49, true);

        var document = await provider.FetchAsync(player);

        Assert.Equal("LRCLIB_19940257", document!.LyricsId);
    }

    [Fact]
    public async Task FetchAsync_RejectsASearchHitWhoseDurationIsWayOff()
    {
        // Same title and artist, but two minutes longer: a different recording, not this track.
        var handler = new RoutedHandler
        {
            OnGet = new HttpResponseMessage(HttpStatusCode.NotFound),
            OnSearch = Json("""
            [{"id": 9, "trackName": "S.E.X.", "artistName": "Madonna",
              "albumName": "Something Else", "duration": 380.0,
              "syncedLyrics": "[00:01.00]not this one"}]
            """),
        };

        using var provider = new LrcLibLyricsProvider(messageHandler: handler);

        Assert.Null(await provider.FetchAsync(Player));
    }

    [Fact]
    public async Task FetchAsync_SkipsResultsThatOnlyHaveUntimedText()
    {
        var handler = new RoutedHandler
        {
            OnGet = Json("""
            {"id": 7, "trackName": "S.E.X.", "artistName": "Madonna", "duration": 251.0,
             "plainLyrics": "just prose", "syncedLyrics": null}
            """),
            OnSearch = Json("[]"),
        };

        using var provider = new LrcLibLyricsProvider(messageHandler: handler);

        Assert.Null(await provider.FetchAsync(Player));
    }

    [Fact]
    public async Task FetchAsync_TreatsAnInstrumentalAsHavingNoLyrics()
    {
        var handler = new RoutedHandler
        {
            OnGet = Json("""
            {"id": 8, "trackName": "S.E.X.", "artistName": "Madonna", "duration": 251.0,
             "instrumental": true, "syncedLyrics": "[00:01.00]ignored"}
            """),
            OnSearch = Json("[]"),
        };

        using var provider = new LrcLibLyricsProvider(messageHandler: handler);

        Assert.Null(await provider.FetchAsync(Player));
    }

    [Fact]
    public async Task FetchAsync_RemembersAnAnswerSoAReplayCostsNoRequest()
    {
        var handler = new RoutedHandler
        {
            OnGet = Json("""
            {"id": 4242, "trackName": "S.E.X.", "artistName": "Madonna", "duration": 251.0,
             "syncedLyrics": "[00:10.00]one"}
            """),
        };

        using var provider = new LrcLibLyricsProvider(messageHandler: handler);

        _ = await provider.FetchAsync(Player);
        _ = await provider.FetchAsync(Player);

        Assert.Equal(1, handler.GetCount);
    }

    [Fact]
    public async Task FetchAsync_ThrowsWhenTheServiceIsUnreachable()
    {
        using var provider = new LrcLibLyricsProvider(messageHandler: new ThrowingHandler());

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.FetchAsync(Player));
    }

    [Fact]
    public async Task FetchAsync_NeedsBothATitleAndAnArtist()
    {
        var handler = new RoutedHandler();
        using var provider = new LrcLibLyricsProvider(messageHandler: handler);

        Assert.Null(await provider.FetchAsync(new PlayerState(null, "Madonna", null, 0, 200, true)));
        Assert.Null(await provider.FetchAsync(new PlayerState("S.E.X.", null, null, 0, 200, true)));
        Assert.Equal(0, handler.GetCount);
    }

    private static HttpResponseMessage Json(string payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) };
    }

    private sealed class RoutedHandler : HttpMessageHandler
    {
        public HttpResponseMessage? OnGet { get; set; }

        public HttpResponseMessage? OnSearch { get; set; }

        public int GetCount { get; private set; }

        public int SearchCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage? response;

            if (path.EndsWith("/search", StringComparison.Ordinal))
            {
                SearchCount++;
                response = OnSearch;
            }
            else
            {
                GetCount++;
                response = OnGet;
            }

            response ??= new HttpResponseMessage(HttpStatusCode.NotFound);

            // Each assertion reads the body, so hand back a fresh message every time.
            return Task.FromResult(new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.Content is null ? string.Empty : response.Content.ReadAsStringAsync(cancellationToken).Result),
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

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
    public async Task FetchAsync_ProjectsPlainLyricsWhenNoTimedTextExists()
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

        var document = await provider.FetchAsync(Player);

        Assert.NotNull(document);
        Assert.Equal("LRCLIB_7", document!.LyricsId);
        Assert.Equal("plain", document.Status);
        Assert.Equal("lrclib-plain:7", document.SourceFile);
        var line = Assert.Single(document.Lines);
        Assert.Equal("just prose", line.Text);
        Assert.Equal(0.0, line.Begin);
        Assert.Equal(251.0, line.End);
        Assert.Equal(1, handler.SearchCount);
    }

    [Fact]
    public async Task FetchAsync_ProjectsCorneliusDropPlainLyrics()
    {
        var handler = new RoutedHandler
        {
            OnGet = Json("""
            {"id": 26483556, "trackName": "Drop", "artistName": "Cornelius",
             "albumName": "Point", "duration": 293.198367, "instrumental": false,
             "plainLyrics": "Ah\n意外 視界 広い\nAh\n固い 固体 拾い\n\n投げる\n跳ねる",
             "syncedLyrics": null}
            """),
        };
        using var provider = new LrcLibLyricsProvider(messageHandler: handler);
        var player = new PlayerState("Drop", "Cornelius", "Point", 3.0, 293.0, true);

        var document = await provider.FetchAsync(player);

        Assert.NotNull(document);
        Assert.Equal("LRCLIB_26483556", document!.LyricsId);
        Assert.Equal("plain", document.Status);
        Assert.Equal(6, document.Lines.Count);
        Assert.Equal("意外 視界 広い", document.Lines[1].Text);
        Assert.Equal(293.198367, document.Lines[^1].End, precision: 6);
    }

    [Fact]
    public async Task FetchAsync_PrefersSyncedSearchResultOverWrongExactPlainLyrics()
    {
        // LRCLIB's real exact result 115521 mixes lyrics from the full "Family Tree" into
        // "Family Tree (Intro)", while search contains a correct synchronized revision.
        var handler = new RoutedHandler
        {
            OnGet = Json("""
            {"id": 115521, "trackName": "Family Tree (Intro)", "artistName": "Ethel Cain",
             "albumName": "Preacher’s Daughter", "duration": 221.0, "instrumental": false,
             "plainLyrics": "These crosses all over my body\nGive myself up to him in offering",
             "syncedLyrics": null}
            """),
            OnSearch = Json("""
            [
              {"id": 115521, "trackName": "Family Tree (Intro)", "artistName": "Ethel Cain",
               "albumName": "Preacher’s Daughter", "duration": 221.0,
               "plainLyrics": "These crosses all over my body\nGive myself up to him in offering",
               "syncedLyrics": null},
              {"id": 11508372, "trackName": "Family Tree (Intro)", "artistName": "Ethel Cain",
               "albumName": "Preacher’s Daughter", "duration": 221.0,
               "plainLyrics": "These crosses all over my body\nAnd Christ, forgive these bones I'm hiding",
               "syncedLyrics": "[00:37.39]These crosses all over my body\n[00:55.28]And Christ, forgive these bones I'm hiding"}
            ]
            """),
        };
        using var provider = new LrcLibLyricsProvider(messageHandler: handler);
        var player = new PlayerState(
            "Family Tree (Intro)",
            "Ethel Cain",
            "Preacher’s Daughter",
            40.0,
            221.0,
            true);

        var document = await provider.FetchAsync(player);

        Assert.Equal("LRCLIB_11508372", document!.LyricsId);
        Assert.Equal("lrclib:11508372", document.SourceFile);
        Assert.DoesNotContain(document.Lines, line =>
            line.Text.Contains("offering", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, handler.GetCount);
        Assert.Equal(1, handler.SearchCount);
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
    public async Task FetchAsync_TreatsMeyouStyleRepeatedVocalizationAsNoLyrics()
    {
        var handler = new RoutedHandler
        {
            OnGet = Json("""
            {"id": 9070948, "trackName": "Meyou", "artistName": "Julia Holter",
             "albumName": "Something in the Room She Moves", "duration": 355.0,
             "instrumental": false,
             "syncedLyrics": "[00:02.10] Me, meyou\n[00:22.88] Me, meyou\n[00:47.44] Me, meyou\n[01:12.99] Me, meyou, you\n[01:40.33] Me, meyou\n[02:13.08] Meyou, meyou\n[02:42.51] Me, meyou\n[03:05.96] Me, meyou\n[03:30.95] Me, meyou\n[03:54.76] Me, meyou\n[04:19.93] Meyou, meyou\n[04:58.96] Me, meyou, ooh\n[05:27.34] Me, meyou"}
            """),
            OnSearch = Json("[]"),
        };

        using var provider = new LrcLibLyricsProvider(messageHandler: handler);
        var player = new PlayerState(
            "Meyou", "Julia Holter", "Something in the Room She Moves", 3.0, 355.0, true);

        Assert.Null(await provider.FetchAsync(player));
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
    public async Task FetchAsync_PersistentCacheSurvivesProviderRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "AppleMusicLyrics.Tests", Guid.NewGuid().ToString("N"));
        var cachePath = Path.Combine(root, "lrclib-cache.json");
        try
        {
            var handler = new RoutedHandler
            {
                OnGet = Json("""
                {"id": 4242, "trackName": "S.E.X.", "artistName": "Madonna", "duration": 251.0,
                 "syncedLyrics": "[00:10.00]persisted line"}
                """),
            };

            using (var first = new LrcLibLyricsProvider(messageHandler: handler, persistentCachePath: cachePath))
            {
                Assert.Equal("LRCLIB_4242", (await first.FetchAsync(Player))!.LyricsId);
            }

            Assert.True(File.Exists(cachePath));
            using var restarted = new LrcLibLyricsProvider(
                messageHandler: new ThrowingHandler(),
                persistentCachePath: cachePath);

            var cached = await restarted.FetchAsync(Player);

            Assert.Equal("LRCLIB_4242", cached!.LyricsId);
            Assert.Equal("persisted line", cached.Lines[0].Text);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FetchAsync_PersistentCacheRemembersCompletedNoLyricsResult()
    {
        var root = Path.Combine(Path.GetTempPath(), "AppleMusicLyrics.Tests", Guid.NewGuid().ToString("N"));
        var cachePath = Path.Combine(root, "lrclib-cache.json");
        try
        {
            using (var first = new LrcLibLyricsProvider(
                messageHandler: new RoutedHandler(),
                persistentCachePath: cachePath))
            {
                Assert.Null(await first.FetchAsync(Player));
            }

            var onlineAgain = new RoutedHandler
            {
                OnGet = Json("""
                {"id": 99, "trackName": "S.E.X.", "artistName": "Madonna", "duration": 251.0,
                 "syncedLyrics": "[00:10.00]should not be requested yet"}
                """),
            };
            using var restarted = new LrcLibLyricsProvider(
                messageHandler: onlineAgain,
                persistentCachePath: cachePath);

            Assert.Null(await restarted.FetchAsync(Player));
            Assert.Equal(0, onlineAgain.GetCount);
            Assert.Equal(0, onlineAgain.SearchCount);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FetchAsync_IgnoresCorruptPersistentCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "AppleMusicLyrics.Tests", Guid.NewGuid().ToString("N"));
        var cachePath = Path.Combine(root, "lrclib-cache.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(cachePath, "not json");
        try
        {
            var handler = new RoutedHandler
            {
                OnGet = Json("""
                {"id": 7, "trackName": "S.E.X.", "artistName": "Madonna", "duration": 251.0,
                 "syncedLyrics": "[00:10.00]fresh line"}
                """),
            };
            using var provider = new LrcLibLyricsProvider(
                messageHandler: handler,
                persistentCachePath: cachePath);

            Assert.Equal("LRCLIB_7", (await provider.FetchAsync(Player))!.LyricsId);
            Assert.Equal(1, handler.GetCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FetchAsync_DoesNotPersistNetworkFailures()
    {
        var root = Path.Combine(Path.GetTempPath(), "AppleMusicLyrics.Tests", Guid.NewGuid().ToString("N"));
        var cachePath = Path.Combine(root, "lrclib-cache.json");

        using var provider = new LrcLibLyricsProvider(
            messageHandler: new ThrowingHandler(),
            persistentCachePath: cachePath);

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.FetchAsync(Player));
        Assert.False(File.Exists(cachePath));
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

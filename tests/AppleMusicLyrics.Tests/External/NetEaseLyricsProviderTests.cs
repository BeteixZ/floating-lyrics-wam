using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Infrastructure.Windows.External;
using Xunit;

namespace AppleMusicLyrics.Tests.External;

public sealed class NetEaseLyricsProviderTests
{
    private static readonly PlayerState Player = new("Example Song", "Example Artist", "Example Album", 1, 200, true);
    private static readonly string MatchingSearch = Search(Song(123));

    [Fact]
    public void EapiEncryption_MatchesIndependentKnownVector()
    {
        // Generated independently with Python cryptography AES/ECB + PKCS7 and hashlib MD5.
        // A fixed ciphertext catches wrong path normalization, framing, key and padding together.
        const string expected = "04AE33D34A93FE3EC22DA8FA305D290AB337D0FE5F36D211DE0D338CC6AA89D0ACBBD9E916537A23C8CDD041F9433D12A33324F604E25E93B1DF874FD1A3CD989733E947F8E96357833C99F92BDCA82A0DD76138C4FA692B64E5C26CBC0F31A851F09FD3C160A8264EA3F059FE0F0975";

        Assert.Equal(expected, NetEaseEapiRequest.Encrypt("/api/song/lyric/v1", "{\"id\":\"123\",\"cp\":\"false\"}"));
    }

    [Fact]
    public async Task FetchAsync_PreservesCookieAndEncryptedRequestContractAndYrcIntervals()
    {
        var handler = new RoutedHandler(MatchingSearch, _ => Lyrics(
            yrc: "{\"t\":0,\"c\":[]}\n[1000,1200](1000,500,0)First (1500,700,0)line\n[5000,800](5000,800,0)Second"));
        using var provider = new NetEaseLyricsProvider(messageHandler: handler);

        var document = await provider.FetchAsync(Player);
        var cached = await provider.FetchAsync(Player);

        Assert.Same(document, cached);
        Assert.Equal("NetEase Cloud Music", provider.Name);
        Assert.Equal("NETEASE_123", document!.LyricsId);
        Assert.Equal("netease:123", document.SourceFile);
        Assert.Equal(200.0, document.DurationSeconds);
        Assert.Collection(document.Lines,
            line => { Assert.Equal("First line", line.Text); Assert.Equal(1, line.Begin); Assert.Equal(2.2, line.End, 8); },
            line => { Assert.Equal("Second", line.Text); Assert.Equal(5, line.Begin); Assert.Equal(5.8, line.End, 8); });
        Assert.Collection(document.Lines[0].Words!,
            word => { Assert.Equal("First ", word.Text); Assert.Equal(1, word.Begin); Assert.Equal(1.5, word.End); },
            word => { Assert.Equal("line", word.Text); Assert.Equal(1.5, word.Begin); Assert.Equal(2.2, word.End, 8); });
        Assert.Equal(2, handler.SearchRequests.Count);
        Assert.All(handler.SearchRequests, request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https", request.Uri.Scheme);
            Assert.Equal("music.163.com", request.Uri.Host);
            Assert.Equal("/api/search/pc", request.Uri.AbsolutePath);
            Assert.Contains("s=Example%20Song%20Example%20Artist", request.Uri.Query);
            Assert.Contains("limit=10", request.Uri.Query);
            Assert.Equal("https://music.163.com/", request.Referrer);
            Assert.Contains("Safari", request.UserAgent);
        });
        Assert.Null(handler.SearchRequests[0].Cookie);
        Assert.Equal("NMTID=anonymous-session", handler.SearchRequests[1].Cookie);
        var lyricsRequest = Assert.Single(handler.LyricsRequests);
        Assert.Equal(HttpMethod.Post, lyricsRequest.Method);
        Assert.Equal(NetEaseEapiRequest.LyricsUrl, lyricsRequest.Uri.AbsoluteUri);
        Assert.Equal("application/x-www-form-urlencoded", lyricsRequest.ContentType);
        Assert.Equal("https://music.163.com/", lyricsRequest.Referrer);
        Assert.Contains("Android", lyricsRequest.UserAgent);
        Assert.Contains("MUSIC_U=", lyricsRequest.Cookie);
        using var payload = DecryptPayload(lyricsRequest.Body!);
        Assert.Equal("123", payload.RootElement.GetProperty("id").GetString());
        Assert.Equal("false", payload.RootElement.GetProperty("cp").GetString());
        Assert.Equal("0", payload.RootElement.GetProperty("yv").GetString());
        Assert.Equal("", payload.RootElement.GetProperty("csrf_token").GetString());
        using var header = JsonDocument.Parse(payload.RootElement.GetProperty("header").GetString()!);
        Assert.Equal("", header.RootElement.GetProperty("MUSIC_U").GetString());
        Assert.Equal("android", header.RootElement.GetProperty("os").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("[1000,0](1000,100,0)invalid interval")]
    [InlineData("[1000,800](1000,800,0)")]
    public async Task FetchAsync_UsesLrcWhenYrcHasNoValidLines(string? yrc)
    {
        using var provider = new NetEaseLyricsProvider(messageHandler: new RoutedHandler(
            MatchingSearch, _ => Lyrics(yrc: yrc, lrc: "[00:01:25]LRC first\n[00:04.000]LRC second\n[00:06.000]")));

        var document = await provider.FetchAsync(Player);

        Assert.Collection(document!.Lines,
            line => { Assert.Equal("LRC first", line.Text); Assert.Equal(1.25, line.Begin); Assert.Equal(4, line.End); },
            line => { Assert.Equal("LRC second", line.Text); Assert.Equal(6, line.End); });
        Assert.All(document.Lines, line => Assert.Null(line.Words));
    }

    [Theory]
    [InlineData("你好，", "世界！")]
    [InlineData("Hello,  ", "world!")]
    public void YrcParser_PreservesAbsoluteWordsWhitespaceAndPunctuation(string first, string second)
    {
        var content = $"[1000,2000](1020,400,0){first}(1800,500,0){second}";

        var line = Assert.Single(NetEaseTimedLyricsParser.Parse(content));

        Assert.Equal(first + second, line.Text);
        Assert.Equal(1, line.Begin);
        Assert.Equal(3, line.End);
        Assert.Collection(line.Words!,
            word => { Assert.Equal(first, word.Text); Assert.Equal(1.02, word.Begin); Assert.Equal(1.42, word.End, 8); },
            word => { Assert.Equal(second, word.Text); Assert.Equal(1.8, word.Begin); Assert.Equal(2.3, word.End, 8); });
    }

    [Fact]
    public void KlyricParser_AccumulatesDurationsAndIncludesSpaceMarkerInPreviousWord()
    {
        var line = Assert.Single(NetEaseTimedLyricsParser.Parse("[1000,2000](0,500)Hello(0,1) (0,1499)world!"));

        Assert.Equal("Hello world!", line.Text);
        Assert.Equal(3, line.End);
        Assert.Collection(line.Words!,
            word => { Assert.Equal("Hello ", word.Text); Assert.Equal(1, word.Begin); Assert.Equal(1.501, word.End, 8); },
            word => { Assert.Equal("world!", word.Text); Assert.Equal(1.501, word.Begin, 8); Assert.Equal(3, word.End, 8); });
    }

    [Theory]
    [InlineData("[1000,2000]Unmarked line")]
    [InlineData("[1000,2000]Unmarked prefix (1000,500,0)word")]
    [InlineData("[1000,2000](1000,0,0)word")]
    public void TimedParser_DoesNotInventWordsForMissingOrIncompleteTiming(string content)
    {
        Assert.Null(Assert.Single(NetEaseTimedLyricsParser.Parse(content)).Words);
    }

    [Fact]
    public async Task FetchAsync_HandlesOlderKlyricWordMarkers()
    {
        using var provider = new NetEaseLyricsProvider(messageHandler: new RoutedHandler(
            MatchingSearch, _ => Lyrics(klyric: "[1000,2000](0,500)Old (0,1500)line", lrc: "[00:01.00]Fallback")));

        var line = Assert.Single((await provider.FetchAsync(Player))!.Lines);

        Assert.Equal("Old line", line.Text);
        Assert.Equal(1, line.Begin);
        Assert.Equal(3, line.End);
    }

    [Fact]
    public async Task FetchAsync_PrefersYtlrcForYrcAndKeepsWordTiming()
    {
        using var provider = new NetEaseLyricsProvider(messageHandler: new RoutedHandler(MatchingSearch, _ => Lyrics(
            yrc: "[1000,1200](1000,500,0)Hello (1500,700,0)world!",
            ytlrc: "[00:01.000]你好，世界！", tlyric: "[00:01.000]旧翻译")));

        var line = Assert.Single((await provider.FetchAsync(Player))!.Lines);

        Assert.Equal("Hello world!", line.Text);
        Assert.Equal("你好，世界！", line.Translation);
        Assert.Equal(2, line.Words!.Count);
        Assert.Equal(1.5, line.Words[1].Begin);
        Assert.Equal(2.2, line.End, 8);
    }

    [Fact]
    public async Task FetchAsync_MatchesFeaturedTitleAndPreservesTheSuppliedTranslation()
    {
        var player = new PlayerState("One and Only (feat. Twista)", "Mariah Carey", "The Emancipation Of Mimi", 0, 194, true);
        var search = Search(new { id = 42, name = "One And Only", duration = 194706,
            artists = new[] { new { name = "Mariah Carey" }, new { name = "Twista" } },
            album = new { name = "The Emancipation of Mimi" } });
        var handler = new RoutedHandler(search, _ => Lyrics(yrc: "[1000,1000](1000,1000,0)Synthetic",
            ytlrc: "[00:01.00]合成译文"));
        using var provider = new NetEaseLyricsProvider(messageHandler: handler);

        var line = Assert.Single((await provider.FetchAsync(player))!.Lines);

        Assert.Equal("合成译文", line.Translation);
        Assert.Equal(new LyricWord(1, 2, "Synthetic"), Assert.Single(line.Words!));
        Assert.Single(handler.LyricsRequests);
        Assert.All(handler.SearchRequests, request => Assert.Contains("s=One%20and%20Only%20Mariah%20Carey", request.Uri.Query));
    }

    [Fact]
    public async Task FetchAsync_FillsPartialYtlrcFromTlyricWithoutOverwritingPreferredTranslation()
    {
        using var provider = new NetEaseLyricsProvider(messageHandler: new RoutedHandler(MatchingSearch, _ => Lyrics(
            yrc: "[1000,800](1000,800,0)Hello!\n[5000,900](5000,900,0)Goodbye!",
            ytlrc: "[00:01.000]优先翻译",
            tlyric: "[00:01.000]旧版首句\n[00:05.000]再见！")));

        var lines = (await provider.FetchAsync(Player))!.Lines;

        Assert.Equal("优先翻译", lines[0].Translation);
        Assert.Equal("再见！", lines[1].Translation);
        Assert.Equal("Hello!", Assert.Single(lines[0].Words!).Text);
        Assert.Equal("Goodbye!", Assert.Single(lines[1].Words!).Text);
        Assert.Equal(5.9, lines[1].End, 8);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("malformed translation")]
    public async Task FetchAsync_FallsBackToTlyricWhenYtlrcIsAbsentOrInvalid(string? ytlrc)
    {
        using var provider = new NetEaseLyricsProvider(messageHandler: new RoutedHandler(MatchingSearch, _ => Lyrics(
            yrc: "[1000,800](1000,800,0)Hello!", ytlrc: ytlrc, tlyric: "[00:01.000]你好！")));

        var line = Assert.Single((await provider.FetchAsync(Player))!.Lines);

        Assert.Equal("你好！", line.Translation);
        Assert.NotNull(line.Words);
    }

    [Fact]
    public async Task FetchAsync_AttachesTlyricToLrcWithoutFabricatingWords()
    {
        using var provider = new NetEaseLyricsProvider(messageHandler: new RoutedHandler(MatchingSearch, _ => Lyrics(
            lrc: "[00:01.000]Hello!\n[00:04.000]Goodbye!",
            tlyric: "[00:01.000]你好！\n[00:04.000]再见！")));

        var lines = (await provider.FetchAsync(Player))!.Lines;

        Assert.Equal("你好！", lines[0].Translation);
        Assert.Equal("再见！", lines[1].Translation);
        Assert.All(lines, line => Assert.Null(line.Words));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("malformed translation")]
    [InlineData("[00:01.000]Hello!")]
    public async Task FetchAsync_KeepsOriginalWhenTranslationIsMissingInvalidOrIdentical(string? translation)
    {
        using var provider = new NetEaseLyricsProvider(messageHandler: new RoutedHandler(MatchingSearch, _ => Lyrics(
            yrc: "[1000,800](1000,800,0)Hello!", ytlrc: translation)));

        var line = Assert.Single((await provider.FetchAsync(Player))!.Lines);

        Assert.Equal("Hello!", line.Text);
        Assert.Null(line.Translation);
        Assert.Equal("Hello!", Assert.Single(line.Words!).Text);
    }

    [Fact]
    public async Task FetchAsync_IgnoresTranslationWithWrongJsonShape()
    {
        const string response = """
            {"code":200,"lrc":{"lyric":"[00:01.000]Hello!"},"tlyric":{"lyric":42},"ytlrc":[]}
            """;
        using var provider = new NetEaseLyricsProvider(messageHandler: new RoutedHandler(MatchingSearch, _ => response));

        var line = Assert.Single((await provider.FetchAsync(Player))!.Lines);

        Assert.Equal("Hello!", line.Text);
        Assert.Null(line.Translation);
    }

    [Fact]
    public async Task FetchAsync_IgnoresNonJsonBootstrapBodyAndReplaysItsCookie()
    {
        var handler = new RoutedHandler(MatchingSearch, _ => Lyrics(lrc: "[00:01.00]Available"))
        {
            BootstrapBody = "<html>Anonymous session cookie established</html>",
        };
        using var provider = new NetEaseLyricsProvider(messageHandler: handler);

        Assert.NotNull(await provider.FetchAsync(Player));
        Assert.Equal(2, handler.SearchRequests.Count);
        Assert.Equal("NMTID=anonymous-session", handler.SearchRequests[1].Cookie);
    }

    [Fact]
    public async Task FetchAsync_RanksCandidatesAndLimitsEmptyDownloadsToThree()
    {
        var handler = new RoutedHandler(Search(Song(4, 203000), Song(2, 201000), Song(1, 200000), Song(3, 202000)), _ => Lyrics());
        using var provider = new NetEaseLyricsProvider(messageHandler: handler);

        Assert.Null(await provider.FetchAsync(Player));

        Assert.Equal(new long[] { 1, 2, 3 }, handler.LyricsRequests.Select(request => ReadSongId(request.Body!)));
    }

    [Fact]
    public async Task FetchAsync_TriesAnotherMatchedRecordingIfFirstHasNoLyrics()
    {
        var handler = new RoutedHandler(Search(Song(2, 201000), Song(1, 200000)),
            id => id == 1 ? Lyrics() : Lyrics(lrc: "[00:03.00]Available"));
        using var provider = new NetEaseLyricsProvider(messageHandler: handler);

        Assert.Equal("NETEASE_2", (await provider.FetchAsync(Player))!.LyricsId);
        Assert.Equal(new long[] { 1, 2 }, handler.LyricsRequests.Select(request => ReadSongId(request.Body!)));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("api")]
    [InlineData("json")]
    public async Task FetchAsync_TriesAnotherMatchedRecordingAfterCandidateFailure(string failure)
    {
        var response = failure switch
        {
            "http" => (HttpStatusCode.BadGateway, "temporary gateway failure"),
            "api" => (HttpStatusCode.OK, "{\"code\":503}"),
            _ => (HttpStatusCode.OK, "not json"),
        };
        var handler = new RoutedHandler(Search(Song(1), Song(2, 201000)), _ => Lyrics(lrc: "[00:03.00]Available"))
        {
            CandidateResponses = new() { [1] = response },
        };
        using var provider = new NetEaseLyricsProvider(messageHandler: handler);

        Assert.Equal("NETEASE_2", (await provider.FetchAsync(Player))!.LyricsId);
        Assert.Equal(new long[] { 1, 2 }, handler.LyricsRequests.Select(request => ReadSongId(request.Body!)));
    }

    [Fact]
    public async Task FetchAsync_AggregatesCandidateFailuresIfNoLyricsSucceeded()
    {
        var handler = new RoutedHandler(Search(Song(1), Song(2, 201000), Song(3, 202000)), _ => Lyrics())
        {
            CandidateResponses = new()
            {
                [1] = (HttpStatusCode.ServiceUnavailable, "temporary failure"),
                [2] = (HttpStatusCode.OK, "not json"),
            },
        };
        using var provider = new NetEaseLyricsProvider(messageHandler: handler);

        var exception = await Assert.ThrowsAsync<AggregateException>(() => provider.FetchAsync(Player));

        Assert.Equal(2, exception.InnerExceptions.Count);
        Assert.IsType<HttpRequestException>(exception.InnerExceptions[0]);
        Assert.IsAssignableFrom<JsonException>(exception.InnerExceptions[1]);
        Assert.Equal(3, handler.LyricsRequests.Count);
    }

    [Fact]
    public async Task FetchAsync_DoesNotTreatMixedFailureAndEmptyCandidatesAsCompletedMiss()
    {
        var handler = new RoutedHandler(Search(Song(1), Song(2, 201000)), _ => Lyrics())
        {
            CandidateResponses = new() { [1] = (HttpStatusCode.ServiceUnavailable, "temporary failure") },
        };
        using var provider = new NetEaseLyricsProvider(messageHandler: handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.FetchAsync(Player));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.FetchAsync(Player));
        Assert.Equal(4, handler.SearchRequests.Count);
        Assert.Equal(4, handler.LyricsRequests.Count);
    }

    [Fact]
    public async Task FetchAsync_RejectsWrongTitlesArtistsDurationsAndRecordingVariants()
    {
        var handler = new RoutedHandler(Search(
            Song(1, title: "Another Song"), Song(2, artist: "Another Artist"),
            Song(3, duration: 240000), Song(4, title: "Example Song (Live)")), _ => Lyrics(lrc: "[00:01.00]Wrong"));
        using var provider = new NetEaseLyricsProvider(messageHandler: handler);

        Assert.Null(await provider.FetchAsync(Player));
        Assert.Empty(handler.LyricsRequests);
    }

    [Fact]
    public async Task FetchAsync_DoesNotCacheAnEmptySearch()
    {
        var handler = new RoutedHandler("{\"code\":200,\"result\":{\"songCount\":0}}", _ => Lyrics());
        using var provider = new NetEaseLyricsProvider(messageHandler: handler);

        Assert.Null(await provider.FetchAsync(Player));
        Assert.Null(await provider.FetchAsync(Player));
        Assert.Equal(4, handler.SearchRequests.Count);
    }

    [Theory]
    [InlineData("nolyric")]
    [InlineData("pureMusic")]
    public async Task FetchAsync_RespectsExplicitNoLyricsFlag(string flag)
    {
        var response = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["code"] = 200,
            [flag] = true,
            ["lrc"] = new { lyric = "[00:01.00]Stale data" },
        });
        using var provider = new NetEaseLyricsProvider(messageHandler: new RoutedHandler(MatchingSearch, _ => response));

        Assert.Null(await provider.FetchAsync(Player));
    }

    [Theory]
    [InlineData("search-first", 503)]
    [InlineData("search", 429)]
    [InlineData("lyrics", 502)]
    public async Task FetchAsync_PropagatesHttpFailures(string failedStage, int statusCode)
    {
        var handler = new RoutedHandler(MatchingSearch, _ => Lyrics(lrc: "[00:01.00]Line"))
        {
            FailedStage = failedStage,
            FailureStatus = (HttpStatusCode)statusCode,
        };
        using var provider = new NetEaseLyricsProvider(messageHandler: handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.FetchAsync(Player));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.FetchAsync(Player));
    }

    [Theory]
    [InlineData("search")]
    [InlineData("lyrics")]
    public async Task FetchAsync_PropagatesApiFailures(string failedStage)
    {
        var handler = new RoutedHandler(MatchingSearch, _ => Lyrics())
        {
            FailedStage = failedStage,
            FailureBody = "{\"code\":503,\"message\":\"temporarily unavailable\"}",
        };
        using var provider = new NetEaseLyricsProvider(messageHandler: handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.FetchAsync(Player));
    }

    [Fact]
    public async Task FetchAsync_PropagatesInvalidJsonRatherThanCachingAMiss()
    {
        using var provider = new NetEaseLyricsProvider(messageHandler: new RoutedHandler(MatchingSearch, _ => "not json"));

        await Assert.ThrowsAnyAsync<JsonException>(() => provider.FetchAsync(Player));
    }

    [Fact]
    public async Task FetchAsync_CancelsAnInFlightRequest()
    {
        using var cancellation = new CancellationTokenSource();
        using var provider = new NetEaseLyricsProvider(messageHandler: new BlockingHandler(cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FetchAsync(Player, cancellation.Token));
    }

    [Fact]
    public async Task FetchAsync_DoesNotAdvanceCandidatesAfterCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RoutedHandler(Search(Song(1), Song(2, 201000)), _ => Lyrics())
        {
            CancelDuringLyrics = cancellation,
        };
        using var provider = new NetEaseLyricsProvider(messageHandler: handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FetchAsync(Player, cancellation.Token));
        Assert.Single(handler.LyricsRequests);
    }

    private static object Song(long id, double duration = 200000, string title = "Example Song", string artist = "Example Artist") =>
        new { id, name = title, duration, artists = new[] { new { name = artist } }, album = new { name = "Example Album" } };

    private static string Search(params object[] songs) => JsonSerializer.Serialize(new { code = 200, result = new { songs } });

    private static string Lyrics(string? yrc = null, string? lrc = null, string? klyric = null, string? ytlrc = null, string? tlyric = null) =>
        JsonSerializer.Serialize(new
        {
            code = 200, yrc = new { lyric = yrc }, lrc = new { lyric = lrc }, klyric = new { lyric = klyric },
            ytlrc = new { lyric = ytlrc }, tlyric = new { lyric = tlyric },
        });

    private static JsonDocument DecryptPayload(string formBody)
    {
        Assert.StartsWith("params=", formBody);
        var ciphertext = Convert.FromHexString(Uri.UnescapeDataString(formBody[7..]));
        using var aes = Aes.Create();
        aes.Key = Encoding.ASCII.GetBytes("e82ckenh8dichen8");
        var plaintext = Encoding.UTF8.GetString(aes.DecryptEcb(ciphertext, PaddingMode.PKCS7));
        var parts = plaintext.Split("-36cd479b6b5-", StringSplitOptions.None);
        Assert.Equal(3, parts.Length);
        Assert.Equal("/api/song/lyric/v1", parts[0]);
        return JsonDocument.Parse(parts[1]);
    }

    private static long ReadSongId(string body)
    {
        using var payload = DecryptPayload(body);
        return long.Parse(payload.RootElement.GetProperty("id").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record RequestSnapshot(HttpMethod Method, Uri Uri, string? Cookie, string? Referrer,
        string UserAgent, string? ContentType, string? Body);

    private sealed class RoutedHandler(string searchJson, Func<long, string> lyricsJson) : HttpMessageHandler
    {
        public List<RequestSnapshot> SearchRequests { get; } = [];
        public List<RequestSnapshot> LyricsRequests { get; } = [];
        public string? FailedStage { get; init; }
        public HttpStatusCode FailureStatus { get; init; } = HttpStatusCode.OK;
        public string? FailureBody { get; init; }
        public string BootstrapBody { get; init; } = "{\"code\":200}";
        public Dictionary<long, (HttpStatusCode Status, string Body)> CandidateResponses { get; init; } = [];
        public CancellationTokenSource? CancelDuringLyrics { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var snapshot = new RequestSnapshot(request.Method, request.RequestUri!,
                request.Headers.TryGetValues("Cookie", out var cookie) ? string.Join("; ", cookie) : null,
                request.Headers.Referrer?.AbsoluteUri, request.Headers.UserAgent.ToString(),
                request.Content?.Headers.ContentType?.MediaType,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            var search = snapshot.Uri.AbsolutePath == "/api/search/pc";
            var firstPass = search && SearchRequests.Count % 2 == 0;
            var stage = search ? firstPass ? "search-first" : "search" : "lyrics";
            if (search) SearchRequests.Add(snapshot); else LyricsRequests.Add(snapshot);
            if (!search)
            {
                if (CancelDuringLyrics is not null)
                {
                    CancelDuringLyrics.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (CandidateResponses.TryGetValue(ReadSongId(snapshot.Body!), out var candidateResponse))
                {
                    return new HttpResponseMessage(candidateResponse.Status)
                    {
                        Content = new StringContent(candidateResponse.Body),
                    };
                }
            }
            var failed = FailedStage == stage;
            var response = new HttpResponseMessage(failed ? FailureStatus : HttpStatusCode.OK)
            {
                Content = new StringContent(failed && FailureBody is not null ? FailureBody
                    : search ? firstPass ? BootstrapBody : searchJson : lyricsJson(ReadSongId(snapshot.Body!))),
            };
            if (firstPass) response.Headers.TryAddWithoutValidation("Set-Cookie", "NMTID=anonymous-session; Max-Age=86400; Path=/; HttpOnly");
            return response;
        }
    }

    private sealed class BlockingHandler(CancellationTokenSource cancellation) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Canceled request unexpectedly completed.");
        }
    }
}

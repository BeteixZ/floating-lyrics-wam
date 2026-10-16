using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Infrastructure.Windows.External;
using Xunit;

namespace AppleMusicLyrics.Tests.External;

public sealed class QQMusicLyricsProviderTests
{
    private static readonly PlayerState Player = new("Example", "Singer", "Album", 0, 20, true);
    private const string Candidate = """{"id":42,"mid":"mid42","name":"Example","singer":[{"name":"Singer"}],"album":{"name":"Album"},"interval":20}""";
    // Synthetic XML, generated independently using a Python transcription of the upstream
    // QQMusicQrcDecrypter.swift operations (revision 6f071990d0c9c6f48d29284db805990bffb912ed),
    // zlib compression and inverse E-D-E transforms. It contains only invented test text.
    private const string EncryptedQrc = "0c8d67dd3e549974b64ed2680459f138553fe2b7cdeb268e33263ce3ef74605977e46a86d6e15cc2c6f0f870f1de0b7d71157fdbbeff4a1b4a55b89001d28f67bcf00bce10c8bd4f463ff92dd2f9dd24556dd319f157313cb9fbbe052fa8bc572a12fcbb78b126f7682b1f1aeee2e98d602a3e4235225714bbe5b0c595851050";

    [Fact]
    public async Task FetchAsync_DecryptsQrcAndUsesPinnedEndpointContracts()
    {
        var handler = new RoutedHandler((request, _) =>
            Task.FromResult(request.Uri.AbsolutePath.Contains("lyric_download", StringComparison.Ordinal)
                ? XmlResponse(EncryptedQrc) : SearchResponse(request, Candidate)));
        using var provider = new QQMusicLyricsProvider(messageHandler: handler);

        var document = await provider.FetchAsync(Player);

        Assert.NotNull(document);
        Assert.Equal("QQMUSIC_42", document!.LyricsId);
        Assert.Equal("qqmusic:42", document.SourceFile);
        Assert.Equal(20, document.DurationSeconds);
        Assert.Collection(document.Lines,
            line => { Assert.Equal("Hello world", line.Text); Assert.Equal(1, line.Begin); Assert.Equal(2.5, line.End); },
            line => { Assert.Equal("Second", line.Text); Assert.Equal(3, line.Begin); Assert.Equal(5, line.End); });
        Assert.Collection(document.Lines[0].Words!,
            word => { Assert.Equal("Hello", word.Text); Assert.Equal(1, word.Begin); Assert.Equal(1.5, word.End); },
            word => { Assert.Equal(" world", word.Text); Assert.Equal(1.5, word.Begin); Assert.Equal(2.5, word.End); });
        Assert.Equal(new LyricWord(3, 5, "Second"), Assert.Single(document.Lines[1].Words!));
        var smartbox = Assert.Single(handler.Requests, request => request.Uri.AbsolutePath.Contains("smartbox", StringComparison.Ordinal));
        Assert.Equal(HttpMethod.Get, smartbox.Method);
        Assert.Contains("key=Example%20Singer", smartbox.Uri.Query);
        var search = Assert.Single(handler.Requests, request => request.Uri.Host == "u.y.qq.com");
        Assert.Equal(HttpMethod.Post, search.Method);
        Assert.Equal("application/json", search.ContentType);
        using (var json = JsonDocument.Parse(search.Body))
        {
            var req = json.RootElement.GetProperty("req_1");
            Assert.Equal("music.search.SearchCgiService", req.GetProperty("module").GetString());
            Assert.Equal("DoSearchForQQMusicDesktop", req.GetProperty("method").GetString());
            Assert.Equal("Example Singer", req.GetProperty("param").GetProperty("query").GetString());
        }
        var lyric = Assert.Single(handler.Requests, request => request.Uri.AbsolutePath.Contains("lyric_download", StringComparison.Ordinal));
        Assert.Equal(HttpMethod.Post, lyric.Method);
        Assert.Equal("application/x-www-form-urlencoded", lyric.ContentType);
        Assert.Equal("https://c.y.qq.com/", lyric.Referrer);
        Assert.Contains("musicid=42", lyric.Body);
        Assert.Contains("version=15", lyric.Body);
        Assert.Contains("miniversion=82", lyric.Body);
        Assert.Contains("lrctype=4", lyric.Body);
        Assert.Same(document, await provider.FetchAsync(Player));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task FetchAsync_RemovesMalformedWrapperMetadataBeforeDecryptingQrc()
    {
        var handler = new RoutedHandler((request, _) => Task.FromResult(
            request.Uri.AbsolutePath.Contains("lyric_download", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<cmd value=\"1031\" verson=\"4\"><miniversion=\"1\" />"
                        + "<metadata value=\"1\" /><content>" + EncryptedQrc + "</content></cmd>",
                        Encoding.UTF8, "application/xml"),
                }
                : SearchResponse(request, Candidate)));
        using var provider = new QQMusicLyricsProvider(messageHandler: handler);
        var document = await provider.FetchAsync(Player);
        Assert.NotNull(document);
        Assert.Equal(new[] { "Hello world", "Second" }, document!.Lines.Select(line => line.Text));
        Assert.Equal(new[] { 1d, 3d }, document.Lines.Select(line => line.Begin));
        Assert.Equal(new[] { 2.5, 5d }, document.Lines.Select(line => line.End));
    }

    [Theory]
    [InlineData("[00:01.00]first\n[00:03.00]second")]
    [InlineData("[00:00:01.00]first\n[00:00:03.00]second")]
    public async Task FetchAsync_ParsesPlainLrcAndExtendedTimestamps(string lyric)
    {
        var handler = SuccessHandler(lyric);
        using var provider = new QQMusicLyricsProvider(messageHandler: handler);
        var document = await provider.FetchAsync(Player);
        Assert.NotNull(document);
        Assert.Equal(new[] { "first", "second" }, document!.Lines.Select(line => line.Text));
        Assert.Equal(1, document.Lines[0].Begin);
        Assert.Equal(3, document.Lines[0].End);
        Assert.Equal(20, document.Lines[1].End);
        Assert.All(document.Lines, line => Assert.Null(line.Words));
    }

    [Fact]
    public async Task FetchAsync_PreservesAbsoluteCjkWordTimesSpacingDurationsAndGaps()
    {
        const string qrc = "[5000,3000]你(5000,300)好(5400,600) (6000,100)world (6500,700)";
        using var provider = new QQMusicLyricsProvider(messageHandler: SuccessHandler(qrc));
        var line = Assert.Single((await provider.FetchAsync(Player))!.Lines);
        Assert.Equal("你好 world ", line.Text);
        Assert.Equal(5, line.Begin);
        Assert.Equal(8, line.End);
        Assert.Collection(line.Words!,
            word => Assert.Equal(new LyricWord(5, 5.3, "你"), word),
            word => Assert.Equal(new LyricWord(5.4, 6, "好"), word),
            word => Assert.Equal(new LyricWord(6, 6.1, " "), word),
            word => Assert.Equal(new LyricWord(6.5, 7.2, "world "), word));
    }

    [Fact]
    public async Task FetchAsync_LeavesWordTimesAbsentWhenQrcOnlyHasLineTags()
    {
        using var provider = new QQMusicLyricsProvider(messageHandler: SuccessHandler("[1000,1000]untimed words"));
        Assert.Null(Assert.Single((await provider.FetchAsync(Player))!.Lines).Words);
    }

    [Fact]
    public async Task FetchAsync_AlignsTranslationLrcAndKeepsOriginalWordTimes()
    {
        const string original = "[1000,1500]Hello(1000,500) world(1500,1000)\n[3000,2000]Second(3000,2000)";
        const string translation = "[00:01.08]你好世界\n[00:03.08]第二行\n[00:15.00]unmatched";
        using var provider = new QQMusicLyricsProvider(messageHandler: SuccessHandler(original, translation));
        var document = (await provider.FetchAsync(Player))!;
        Assert.Equal(2, document.Lines.Count);
        Assert.Equal("你好世界", document.Lines[0].Translation);
        Assert.Equal("第二行", document.Lines[1].Translation);
        Assert.Equal(new[] { "Hello", " world" }, document.Lines[0].Words!.Select(word => word.Text));
        Assert.Equal(new LyricWord(3, 5, "Second"), Assert.Single(document.Lines[1].Words!));
    }

    [Fact]
    public async Task FetchAsync_DecryptsTranslationQrcWithoutReplacingOriginalText()
    {
        const string original = "[1000,1500]你好(1000,1500)\n[3000,2000]第二行(3000,2000)";
        using var provider = new QQMusicLyricsProvider(messageHandler: SuccessHandler(original, EncryptedQrc));
        var document = (await provider.FetchAsync(Player))!;
        Assert.Equal("你好", document.Lines[0].Text);
        Assert.Equal("Hello world", document.Lines[0].Translation);
        Assert.Equal("Second", document.Lines[1].Translation);
        Assert.Equal(new LyricWord(1, 2.5, "你好"), Assert.Single(document.Lines[0].Words!));
    }

    [Fact]
    public async Task FetchAsync_SkipsDuplicateTranslationText()
    {
        using var provider = new QQMusicLyricsProvider(messageHandler: SuccessHandler(
            "[1000,1500]Hello(1000,1500)", "[00:01.00]Hello"));
        var line = Assert.Single((await provider.FetchAsync(Player))!.Lines);
        Assert.Equal("Hello", line.Text);
        Assert.Null(line.Translation);
        Assert.Equal(new LyricWord(1, 2.5, "Hello"), Assert.Single(line.Words!));
    }

    [Theory]
    [InlineData("aabb")]
    [InlineData("<broken xml")]
    [InlineData("<!DOCTYPE qrc [<!ENTITY entity SYSTEM 'file:///nonexistent'>]><qrc LyricContent=\"[1000,500]&entity;\"/>")]
    public async Task FetchAsync_KeepsUsableOriginalWhenOptionalTranslationIsMalformed(string translation)
    {
        using var provider = new QQMusicLyricsProvider(messageHandler: SuccessHandler(
            "[1000,1500]Hello(1000,1500)", translation));
        var line = Assert.Single((await provider.FetchAsync(Player))!.Lines);
        Assert.Equal("Hello", line.Text);
        Assert.Null(line.Translation);
        Assert.Equal(new LyricWord(1, 2.5, "Hello"), Assert.Single(line.Words!));
    }

    [Fact]
    public async Task FetchAsync_ParsesNestedPlainQrcPreservingOriginalTextAndExplicitEnd()
    {
        var nested = new XElement("QrcInfos", new XElement("LyricInfo", new XElement("Lyric_1",
            new XAttribute("LyricContent", "[1000,1500]你好(1000,500) & \"world\"(1500,1000)\n[4000,500]end(4000,500)")))).ToString();
        using var provider = new QQMusicLyricsProvider(messageHandler: SuccessHandler(nested));
        var document = await provider.FetchAsync(Player);
        Assert.Equal("你好 & \"world\"", document!.Lines[0].Text);
        Assert.Equal(2.5, document.Lines[0].End);
        Assert.Equal(4, document.Lines[1].Begin);
        Assert.Equal(4.5, document.Lines[1].End);
    }

    [Fact]
    public async Task FetchAsync_ToleratesRawAttributeQuotesAmpersandsAndAdjacentQrcTags()
    {
        const string nested = """
            <?xml version="1.0"?><QrcInfos><Lyric_1 LyricType="1" LyricContent="[1000,500]Rock & "roll"(1000,500)[2000,500]second(2000,500)  [3000,500]third(3000,500)
            [4000,500]fourth(4000,500)" /></QrcInfos>
            """;
        using var provider = new QQMusicLyricsProvider(messageHandler: SuccessHandler(nested));
        var document = await provider.FetchAsync(Player);
        Assert.Equal(new[] { "Rock & \"roll\"", "second", "third", "fourth" }, document!.Lines.Select(line => line.Text));
        Assert.Equal(new[] { 1d, 2d, 3d, 4d }, document.Lines.Select(line => line.Begin));
        Assert.Equal(new[] { 1.5, 2.5, 3.5, 4.5 }, document.Lines.Select(line => line.End));
    }

    [Fact]
    public async Task FetchAsync_RetainsMusicuFallbackWhenSmartboxFails()
    {
        var handler = new RoutedHandler((request, _) => Task.FromResult(
            request.Uri.AbsolutePath.Contains("smartbox", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : request.Uri.Host == "u.y.qq.com" ? SearchResponse(request, Candidate)
                : XmlResponse("[00:01]fallback")));
        using var provider = new QQMusicLyricsProvider(messageHandler: handler);
        Assert.Equal("fallback", (await provider.FetchAsync(Player))!.Lines[0].Text);
    }

    [Fact]
    public async Task FetchAsync_RejectsRemixHiddenBySmartboxWhenMusicuFails()
    {
        const string lyric = "[ti:Example (MAKJ Remix版)]\r\n[ar:Singer]\r\n[1000,500]wrong recording(1000,500)";
        var handler = new RoutedHandler((request, _) => Task.FromResult(
            request.Uri.Host == "u.y.qq.com" ? JsonResponse("{\"req_1\":{\"code\":2001}}")
                : request.Uri.AbsolutePath.Contains("lyric_download", StringComparison.Ordinal) ? XmlResponse(lyric)
                : SearchResponse(request, Candidate)));
        using var provider = new QQMusicLyricsProvider(messageHandler: handler);
        Assert.Null(await provider.FetchAsync(Player));
        Assert.Single(handler.Requests, request => request.Uri.AbsolutePath.Contains("lyric_download", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchAsync_TriesNextCandidateAfterReturnedRecordingMetadataDisagrees()
    {
        var candidates = Candidate + "," + Candidate.Replace("\"id\":42", "\"id\":43", StringComparison.Ordinal);
        var handler = new RoutedHandler((request, _) => Task.FromResult(
            request.Uri.AbsolutePath.Contains("lyric_download", StringComparison.Ordinal)
                ? XmlResponse(request.Body.Contains("musicid=42", StringComparison.Ordinal)
                    ? "[ti:Example (Remix)]\n[ar:Singer]\n[1000,500]wrong recording(1000,500)"
                    : "[ti:Example]\n[ar:Singer]\n[1000,500]right recording(1000,500)")
                : SearchResponse(request, candidates)));
        using var provider = new QQMusicLyricsProvider(messageHandler: handler);
        var document = await provider.FetchAsync(Player);
        Assert.Equal("qqmusic:43", document!.SourceFile);
        Assert.Equal("right recording", Assert.Single(document.Lines).Text);
        Assert.Equal(2, handler.Requests.Count(request => request.Uri.AbsolutePath.Contains("lyric_download", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FetchAsync_AcceptsMatchingRecordingVariantMetadata()
    {
        var remixPlayer = Player with { Title = "Example (Remix)" };
        var candidate = Candidate.Replace("\"name\":\"Example\"", "\"name\":\"Example (Remix)\"", StringComparison.Ordinal);
        var handler = new RoutedHandler((request, _) => Task.FromResult(
            request.Uri.AbsolutePath.Contains("lyric_download", StringComparison.Ordinal)
                ? XmlResponse("[ti:Example (Remix)]\n[ar:Singer]\n[1000,500]matching variant(1000,500)")
                : SearchResponse(request, candidate)));
        using var provider = new QQMusicLyricsProvider(messageHandler: handler);
        Assert.Equal("matching variant", Assert.Single((await provider.FetchAsync(remixPlayer))!.Lines).Text);
    }

    [Fact]
    public async Task FetchAsync_MatchesFeaturedTitleThroughSearchAndQrcMetadataAndRetainsTranslation()
    {
        var player = new PlayerState("One and Only (feat. Twista)", "Mariah Carey", "The Emancipation Of Mimi", 0, 194, true);
        const string candidate = """{"id":42,"name":"One And Only","singer":[{"name":"Mariah Carey"},{"name":"Twista"}],"album":{"name":"The Emancipation of Mimi"},"interval":194}""";
        var handler = new RoutedHandler((request, _) => Task.FromResult(
            request.Uri.AbsolutePath.Contains("lyric_download", StringComparison.Ordinal)
                ? XmlResponse("[ti:One and only]\r\n[ar:Mariah Carey]\r\n[1000,1000]Synthetic(1000,1000)",
                    "[00:01.00]合成译文")
                : request.Uri.Host == "u.y.qq.com" ? SearchResponse(request, candidate)
                : JsonResponse("{\"code\":0,\"data\":{\"song\":{\"itemlist\":[]}}}")));
        using var provider = new QQMusicLyricsProvider(messageHandler: handler);

        var line = Assert.Single((await provider.FetchAsync(player))!.Lines);

        Assert.Equal("合成译文", line.Translation);
        Assert.Equal(new LyricWord(1, 2, "Synthetic"), Assert.Single(line.Words!));
        Assert.Single(handler.Requests, request => request.Uri.AbsolutePath.Contains("lyric_download", StringComparison.Ordinal));
        Assert.Contains("key=One%20and%20Only%20Mariah%20Carey", Assert.Single(handler.Requests,
            request => request.Uri.AbsolutePath.Contains("smartbox", StringComparison.Ordinal)).Uri.Query);
    }

    [Fact]
    public async Task FetchAsync_RejectsConflictingGuestCreditsInReturnedQrcMetadata()
    {
        using var provider = new QQMusicLyricsProvider(messageHandler: SuccessHandler(
            "[ti:Example (feat. Another Guest)]\r\n[ar:Singer]\r\n[1000,1000]Synthetic(1000,1000)"));
        Assert.Null(await provider.FetchAsync(Player with { Title = "Example (feat. Guest)" }));
    }

    [Fact]
    public async Task FetchAsync_AcceptsLyricsWithoutRecordingTags()
    {
        using var provider = new QQMusicLyricsProvider(messageHandler: SuccessHandler("[1000,500]no metadata(1000,500)"));
        Assert.Equal("no metadata", Assert.Single((await provider.FetchAsync(Player))!.Lines).Text);
    }

    [Fact]
    public async Task FetchAsync_UsesOnlyOriginalRecordingTagsForValidation()
    {
        using var provider = new QQMusicLyricsProvider(messageHandler: SuccessHandler(
            "[ti:Example]\n[ar:Singer]\n[1000,500]original(1000,500)",
            "[ti:Translated title]\n[ar:Translator]\n[00:01.00]translation"));
        var line = Assert.Single((await provider.FetchAsync(Player))!.Lines);
        Assert.Equal("original", line.Text);
        Assert.Equal("translation", line.Translation);
    }

    [Theory]
    [InlineData("Example song title delivery")]
    [InlineData("Example song title remixed")]
    public void RecordingVariantMatching_DoesNotTreatPartsOfEnglishWordsAsVariantTokens(string title)
    {
        Assert.NotEqual(int.MinValue, PlatformLyricsSupport.ScoreCandidate(
            Player with { Title = "Example song title" }, title, "Singer", "Album", 20));
    }

    [Fact]
    public void RecordingVariantMatching_RejectsRemixTokenAdjacentToChineseSuffix()
    {
        Assert.Equal(int.MinValue, PlatformLyricsSupport.ScoreCandidate(
            Player with { Title = "Example song title" }, "Example song title (MAKJ Remix版)",
            "Singer", "Album", 20));
    }

    [Theory]
    [InlineData("[ti:Different song]\r\n")]
    [InlineData("[ar:Different artist]\r\n")]
    public async Task FetchAsync_RejectsPresentTitleOrArtistTagWhileMissingFieldFallsBack(string metadata)
    {
        using var provider = new QQMusicLyricsProvider(messageHandler: SuccessHandler(
            metadata + "[1000,500]mismatch(1000,500)"));
        Assert.Null(await provider.FetchAsync(Player));
    }

    [Fact]
    public async Task FetchAsync_DeduplicatesNumericIdsAndPrefersKnownRecordingDuration()
    {
        var handler = SuccessHandler("[00:01]first");
        using var provider = new QQMusicLyricsProvider(messageHandler: handler);
        var document = await provider.FetchAsync(Player);
        Assert.Equal(20, document!.DurationSeconds);
        Assert.Single(handler.Requests, request => request.Uri.AbsolutePath.Contains("lyric_download", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchAsync_RejectsWrongArtistsRecordingVariantsAndDuration()
    {
        const string wrong = """
            {"id":1,"name":"Example","singer":[{"name":"Another artist"}],"interval":20},
            {"id":2,"name":"Example (Live)","singer":[{"name":"Singer"}],"interval":20},
            {"id":3,"name":"Example","singer":[{"name":"Singer"}],"interval":80}
            """;
        var handler = new RoutedHandler((request, _) => Task.FromResult(
            request.Uri.Host == "u.y.qq.com" ? SearchResponse(request, wrong) : JsonResponse("{\"code\":0,\"data\":{\"song\":{\"itemlist\":[]}}}")));
        using var provider = new QQMusicLyricsProvider(messageHandler: handler);
        Assert.Null(await provider.FetchAsync(Player));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task FetchAsync_LimitsLyricFetchesToTopThreeAndPropagatesFailure()
    {
        var rows = string.Join(',', Enumerable.Range(1, 5).Select(id => Candidate.Replace("\"id\":42", $"\"id\":{id}", StringComparison.Ordinal)));
        var handler = new RoutedHandler((request, _) => Task.FromResult(
            request.Uri.AbsolutePath.Contains("lyric_download", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : request.Uri.Host == "u.y.qq.com" ? SearchResponse(request, rows)
                : JsonResponse("{\"code\":0,\"data\":{\"song\":{\"itemlist\":[]}}}")));
        using var provider = new QQMusicLyricsProvider(messageHandler: handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.FetchAsync(Player));
        Assert.Equal(3, handler.Requests.Count(request => request.Uri.AbsolutePath.Contains("lyric_download", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FetchAsync_PropagatesCancellationToBothSearchRequests()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var handler = new RoutedHandler(async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref count) == 2) started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Delay must be cancelled.");
        });
        using var provider = new QQMusicLyricsProvider(messageHandler: handler);
        using var cancellation = new CancellationTokenSource();
        var fetch = provider.FetchAsync(Player, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task FetchAsync_ThrowsWhenBothSearchEndpointsFail()
    {
        var handler = new RoutedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        using var provider = new QQMusicLyricsProvider(messageHandler: handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.FetchAsync(Player));
    }

    [Fact]
    public async Task FetchAsync_TreatsNonzeroServiceCodesAsRetryableFailures()
    {
        var handler = new RoutedHandler((request, _) => Task.FromResult(JsonResponse(
            request.Uri.Host == "u.y.qq.com" ? "{\"req_1\":{\"code\":-1}}" : "{\"code\":-1}")));
        using var provider = new QQMusicLyricsProvider(messageHandler: handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.FetchAsync(Player));
    }

    [Fact]
    public async Task FetchAsync_RetriesIncompleteSearchWhenOtherEndpointHasNoCandidates()
    {
        var handler = new RoutedHandler((request, _) => Task.FromResult(
            request.Uri.Host == "u.y.qq.com" ? SearchResponse(request, null)
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var provider = new QQMusicLyricsProvider(messageHandler: handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.FetchAsync(Player));
    }

    [Fact]
    public async Task FetchAsync_LeavesCompletedEmptyResultsUncached()
    {
        var handler = new RoutedHandler((request, _) => Task.FromResult(SearchResponse(request, null)));
        using var provider = new QQMusicLyricsProvider(messageHandler: handler);
        Assert.Null(await provider.FetchAsync(Player));
        Assert.Null(await provider.FetchAsync(Player));
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public void ParseResponse_RejectsDtdAndMalformedCipher()
    {
        Assert.Throws<XmlException>(() => QrcLyricsParser.ParseResponse(
            "<!DOCTYPE root [<!ENTITY unsafe SYSTEM 'file:///nonexistent'>]><root><content>&unsafe;</content></root>", "1", 20));
        var nestedDtd = new XElement("root", new XElement("content",
            "<!DOCTYPE root [<!ENTITY unsafe SYSTEM 'file:///nonexistent'>]><root LyricContent=\"[1000,500]&unsafe;(1000,500)\"/>"));
        Assert.Throws<XmlException>(() => QrcLyricsParser.ParseResponse(nestedDtd.ToString(), "1", 20));
        Assert.Throws<FormatException>(() => QrcLyricsDecoder.Decode("aabb"));
    }

    private static RoutedHandler SuccessHandler(string lyric, string? translation = null) => new((request, _) => Task.FromResult(
        request.Uri.AbsolutePath.Contains("lyric_download", StringComparison.Ordinal) ? XmlResponse(lyric, translation)
            : SearchResponse(request, Candidate)));

    private static HttpResponseMessage SearchResponse(Request request, string? rows)
        => request.Uri.Host == "u.y.qq.com"
            ? JsonResponse("{\"req_1\":{\"code\":0,\"data\":{\"body\":{\"song\":{\"list\":[" + rows + "]}}}}}")
            : JsonResponse("{\"code\":0,\"data\":{\"song\":{\"itemlist\":[" + (rows is null ? string.Empty
                : "{\"id\":\"42\",\"mid\":\"mid42\",\"name\":\"Example\",\"singer\":\"Singer\"}") + "]}}}");

    private static HttpResponseMessage XmlResponse(string content, string? translation = null) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("<!--" + new XElement("root", new XElement("content", content),
            translation is null ? null : new XElement("contentts", translation)).ToString() + "-->", Encoding.UTF8, "application/xml"),
    };
    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed record Request(Uri Uri, HttpMethod Method, string Body, string? ContentType, string? Referrer);
    private sealed class RoutedHandler(Func<Request, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly List<Request> _requests = [];
        public IReadOnlyList<Request> Requests { get { lock (_requests) return _requests.ToArray(); } }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var info = new Request(request.RequestUri!, request.Method,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Content?.Headers.ContentType?.MediaType, request.Headers.Referrer?.ToString());
            lock (_requests) _requests.Add(info);
            return await respond(info, cancellationToken);
        }
    }
}

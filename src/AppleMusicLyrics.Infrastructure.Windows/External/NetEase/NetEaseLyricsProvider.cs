// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from MxIris-LyricsX-Project/LyricsKit at
// 6f071990d0c9c6f48d29284db805990bffb912ed,
// Sources/LyricsService/Provider/Services/NetEase.swift.

using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AppleMusicLyrics.Core.Matching;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Parsing;

namespace AppleMusicLyrics.Infrastructure.Windows.External;

public sealed class NetEaseLyricsProvider : PlatformLyricsProvider
{
    private const string SearchUrl = "https://music.163.com/api/search/pc";
    private const string SearchUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.4 Safari/605.1.15";
    private readonly LrcLyricsParser _lrcParser = new();

    public NetEaseLyricsProvider(double timeoutSeconds = 6.0, HttpMessageHandler? messageHandler = null)
        : base(timeoutSeconds, messageHandler)
    {
    }

    public override string Name => "NetEase Cloud Music";

    protected override async Task<LyricsDocument?> FetchCoreAsync(PlayerState player, CancellationToken cancellationToken)
    {
        var candidates = await SearchAsync(player, cancellationToken).ConfigureAwait(false);
        var failures = new List<Exception>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var document = await FetchCandidateAsync(player, candidate, cancellationToken).ConfigureAwait(false);
                if (document is not null)
                {
                    return document;
                }
            }
            catch (Exception error) when (error is HttpRequestException or JsonException)
            {
                // A faulty candidate must not hide a usable matched recording later in the list.
                // Retain failures so a fully unsuccessful lookup is retried by the runtime.
                failures.Add(error);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        if (failures.Count > 1)
        {
            throw new AggregateException("NetEase lyric candidates failed to download.", failures);
        }
        return null;
    }

    private async Task<LyricsDocument?> FetchCandidateAsync(PlayerState player, Candidate candidate, CancellationToken cancellationToken)
    {
        using var request = NetEaseEapiRequest.Create(candidate.Id);
        using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var json = await ReadResponseAsync(response, cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        if ((root.TryGetProperty("nolyric", out var noLyric) && noLyric.ValueKind == JsonValueKind.True)
            || (root.TryGetProperty("pureMusic", out var instrumental) && instrumental.ValueKind == JsonValueKind.True))
        {
            return null;
        }
        var lines = NetEaseTimedLyricsParser.Parse(ReadLyrics(root, "yrc"));
        var usingYrc = lines.Count > 0;
        if (lines.Count == 0)
        {
            lines = NetEaseTimedLyricsParser.Parse(ReadLyrics(root, "klyric"));
        }
        if (lines.Count == 0)
        {
            lines = _lrcParser.ParseLines(ReadLyrics(root, "lrc") ?? "", candidate.Duration);
        }
        if (lines.Count == 0 || MetadataMatching.IsRepetitiveVocalizationOnly(player.Title, lines.Select(line => line.Text)))
        {
            return null;
        }

        // The YRC endpoint can supply its aligned translation in ytlrc; older responses only
        // expose tlyric, the field used by the pinned upstream model. Both are timed LRC text.
        var translationFields = usingYrc ? new[] { "ytlrc", "tlyric" } : new[] { "tlyric", "ytlrc" };
        foreach (var field in translationFields)
        {
            var translationText = ReadLyrics(root, field);
            if (string.IsNullOrWhiteSpace(translationText))
            {
                continue;
            }
            var translations = _lrcParser.ParseLines(translationText, candidate.Duration);
            if (translations.Count > 0)
            {
                // The merger retains translations already assigned by the preferred field,
                // allowing the older field to fill rows omitted from a partial ytlrc response.
                lines = LyricsTranslationMerger.MergeLines(lines, translations);
            }
        }

        return new LyricsDocument(
            LyricsId: $"NETEASE_{candidate.Id}",
            Status: "success",
            SourceFile: $"netease:{candidate.Id}",
            UpdatedAt: DateTimeOffset.UtcNow,
            Lines: lines,
            DurationSeconds: candidate.Duration);
    }

    private async Task<IReadOnlyList<Candidate>> SearchAsync(PlayerState player, CancellationToken cancellationToken)
    {
        var url = $"{SearchUrl}?s={Uri.EscapeDataString(PlatformLyricsSupport.SearchTerm(player))}&offset=0&limit=10&type=1";
        string? cookie = null;
        using (var firstRequest = CreateSearchRequest(url, null))
        using (var firstResponse = await HttpClient.SendAsync(firstRequest, cancellationToken).ConfigureAwait(false))
        {
            // Bootstrap establishes the anonymous session. Upstream intentionally ignores this
            // body's format: a successful cookie-setting response may contain HTML instead of JSON.
            firstResponse.EnsureSuccessStatusCode();
            if (firstResponse.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                // As in the upstream provider, the second request replays the first session cookie.
                cookie = cookies.FirstOrDefault()?.Split(';', 2)[0];
            }
        }

        using var request = CreateSearchRequest(url, cookie);
        using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var json = await ReadResponseAsync(response, cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        if (!root.TryGetProperty("result", out var result) || result.ValueKind == JsonValueKind.Null
            || !result.TryGetProperty("songs", out var songs) || songs.ValueKind == JsonValueKind.Null)
        {
            return [];
        }
        if (songs.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("NetEase search songs was not an array.");
        }

        var candidates = new List<Candidate>();
        foreach (var song in songs.EnumerateArray())
        {
            if (!song.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id) || id <= 0)
            {
                continue;
            }
            var title = ReadString(song, "name");
            var artist = song.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array
                ? string.Join(" / ", artists.EnumerateArray().Select(artist => ReadString(artist, "name")).Where(name => !string.IsNullOrWhiteSpace(name)))
                : null;
            var album = song.TryGetProperty("album", out var albumElement) && albumElement.ValueKind == JsonValueKind.Object
                ? ReadString(albumElement, "name") : null;
            double? duration = song.TryGetProperty("duration", out var durationElement) && durationElement.TryGetDouble(out var milliseconds)
                && double.IsFinite(milliseconds) && milliseconds > 0 ? milliseconds / 1000.0 : null;
            var score = PlatformLyricsSupport.ScoreCandidate(player, title, artist, album, duration);
            if (score != int.MinValue)
            {
                candidates.Add(new Candidate(id, duration, score));
            }
        }

        return candidates.OrderByDescending(candidate => candidate.Score).DistinctBy(candidate => candidate.Id).Take(3).ToArray();
    }

    private static HttpRequestMessage CreateSearchRequest(string url, string? cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent([]),
        };
        request.Headers.Referrer = new Uri("https://music.163.com/");
        request.Headers.TryAddWithoutValidation("User-Agent", SearchUserAgent);
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }
        return request;
    }

    private static async Task<JsonDocument> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            json.Dispose();
            throw new JsonException("NetEase returned a non-object response.");
        }
        if (root.TryGetProperty("code", out var code) && (code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var value) || value != 200))
        {
            var message = code.GetRawText();
            json.Dispose();
            throw new HttpRequestException($"NetEase API returned code {message}.");
        }
        return json;
    }

    private static string? ReadLyrics(JsonElement root, string property) =>
        root.TryGetProperty(property, out var lyrics) && lyrics.ValueKind == JsonValueKind.Object
            ? ReadString(lyrics, "lyric") : null;

    private static string? ReadString(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private sealed record Candidate(long Id, double? Duration, int Score);
}

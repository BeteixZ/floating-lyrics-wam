// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Adapted from MxIris-LyricsX-Project/LyricsKit, revision 6f071990d0c9c6f48d29284db805990bffb912ed.
// Source: Sources/LyricsService/Provider/Services/QQMusic.swift
// Source models: Sources/LyricsService/Model/QQMusic/QQResponseSearchResult.swift

using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Infrastructure.Windows.External;

public sealed class QQMusicLyricsProvider : PlatformLyricsProvider
{
    private const string SmartboxUrl = "https://c.y.qq.com/splcloud/fcgi-bin/smartbox_new.fcg";
    private const string MusicuUrl = "https://u.y.qq.com/cgi-bin/musicu.fcg";
    private const string LyricsUrl = "https://c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg";

    public QQMusicLyricsProvider(double timeoutSeconds = 6.0, HttpMessageHandler? messageHandler = null)
        : base(timeoutSeconds, messageHandler) { }

    public override string Name => "QQ Music";

    protected override async Task<LyricsDocument?> FetchCoreAsync(PlayerState player, CancellationToken cancellationToken)
    {
        var searches = await Task.WhenAll(
            SearchAsync(player, smartbox: true, cancellationToken),
            SearchAsync(player, smartbox: false, cancellationToken)).ConfigureAwait(false);
        if (searches.All(result => result.Failure is not null))
        {
            throw new HttpRequestException("Both QQ Music search endpoints failed.",
                new AggregateException(searches.Select(result => result.Failure!)));
        }

        var candidates = searches.SelectMany(result => result.Candidates)
            .GroupBy(candidate => candidate.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(candidate => candidate.Duration.HasValue).First())
            .Select(candidate => (Candidate: candidate, Score: PlatformLyricsSupport.ScoreCandidate(
                player, candidate.Title, candidate.Artist, candidate.Album, candidate.Duration)))
            .Where(item => item.Score != int.MinValue)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Candidate.Id, StringComparer.Ordinal)
            .Take(3)
            .ToArray();
        if (candidates.Length == 0 && searches.Any(result => result.Failure is not null))
        {
            throw new HttpRequestException("QQ Music search was incomplete and returned no matching candidates.",
                new AggregateException(searches.Where(result => result.Failure is not null).Select(result => result.Failure!)));
        }

        var failures = new List<Exception>();
        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var candidate = item.Candidate;
                using var request = new HttpRequestMessage(HttpMethod.Post, LyricsUrl);
                request.Headers.Referrer = new Uri("https://c.y.qq.com/");
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["musicid"] = candidate.Id,
                    ["version"] = "15",
                    ["miniversion"] = "82",
                    ["lrctype"] = "4",
                });
                using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var document = QrcLyricsParser.ParseResponse(content, candidate.Id,
                    candidate.Duration ?? (player.Duration > 0 ? player.Duration : null), player);
                if (document is { Lines.Count: > 0 })
                {
                    return document;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or FormatException
                or System.Xml.XmlException or DecoderFallbackException)
            {
                failures.Add(ex);
            }
        }
        if (failures.Count > 0)
        {
            throw new HttpRequestException("QQ Music lyric requests did not complete successfully.",
                new AggregateException(failures));
        }
        return null;
    }

    private async Task<SearchResult> SearchAsync(PlayerState player, bool smartbox, CancellationToken cancellationToken)
    {
        try
        {
            using var request = smartbox
                ? new HttpRequestMessage(HttpMethod.Get, SmartboxUrl + "?key=" + Uri.EscapeDataString(PlatformLyricsSupport.SearchTerm(player)))
                : new HttpRequestMessage(HttpMethod.Post, MusicuUrl);
            if (!smartbox)
            {
                var body = new
                {
                    req_1 = new
                    {
                        method = "DoSearchForQQMusicDesktop",
                        module = "music.search.SearchCgiService",
                        param = new { num_per_page = 20, page_num = 1, query = PlatformLyricsSupport.SearchTerm(player), search_type = 0 },
                    },
                };
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }
            using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var result = smartbox ? root : Property(root, "req_1");
            if (Property(result, "code") is { ValueKind: JsonValueKind.Number } code && code.GetInt32() != 0)
            {
                return new SearchResult([], new HttpRequestException(
                    $"QQ Music {(smartbox ? "smartbox" : "musicu")} search returned service code {code.GetInt32()}."));
            }
            var song = smartbox ? Property(Property(result, "data"), "song")
                : Property(Property(Property(result, "data"), "body"), "song");
            var list = Property(song, smartbox ? "itemlist" : "list");
            if (list.ValueKind != JsonValueKind.Array)
            {
                return new SearchResult([], null);
            }
            var candidates = new List<Candidate>();
            foreach (var item in list.EnumerateArray().Take(40))
            {
                var id = StringValue(Property(item, "id"));
                if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId) || numericId <= 0)
                {
                    continue;
                }
                var title = StringValue(Property(item, "name")) ?? StringValue(Property(item, "songname"));
                var singers = Property(item, "singer");
                var artist = singers.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", singers.EnumerateArray().Select(singer => StringValue(Property(singer, "name"))).Where(name => !string.IsNullOrWhiteSpace(name)))
                    : StringValue(singers);
                var album = StringValue(Property(Property(item, "album"), "name"))
                    ?? StringValue(Property(item, "albumname"));
                var interval = Property(item, "interval");
                double? duration = interval.ValueKind == JsonValueKind.Number && interval.TryGetDouble(out var seconds)
                    && double.IsFinite(seconds) && seconds > 0 ? seconds : null;
                candidates.Add(new Candidate(numericId.ToString(CultureInfo.InvariantCulture), title, artist, album, duration));
            }
            return new SearchResult(candidates, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or FormatException)
        {
            return new SearchResult([], ex);
        }
    }

    private static JsonElement Property(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    private static string? StringValue(JsonElement element)
        => element.ValueKind == JsonValueKind.String ? element.GetString()
            : element.ValueKind == JsonValueKind.Number ? element.GetRawText() : null;

    private sealed record Candidate(string Id, string? Title, string? Artist, string? Album, double? Duration);
    private sealed record SearchResult(IReadOnlyList<Candidate> Candidates, Exception? Failure);
}

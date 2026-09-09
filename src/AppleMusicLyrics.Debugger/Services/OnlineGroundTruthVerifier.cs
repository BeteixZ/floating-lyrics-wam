using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppleMusicLyrics.Core.Matching;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Debugger.Models;

namespace AppleMusicLyrics.Debugger.Services;

public sealed partial class OnlineGroundTruthVerifier : IGroundTruthVerifier, IDisposable
{
    private readonly HttpClient _httpClient;
    private static readonly Regex LrcTimeTagRegex = MyRegex();

    public OnlineGroundTruthVerifier(double timeoutSeconds = 6.0)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 2.0, 15.0))
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleMusicLyrics-Debugger/1.0");
    }

    public async Task<GroundTruthResult> QueryGroundTruthAsync(PlayerState player, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(player.Title) || string.IsNullOrWhiteSpace(player.Artist))
        {
            return new GroundTruthResult(false, "None", null, null, null, Array.Empty<string>());
        }

        // 1. Try LRCLIB first
        try
        {
            var lrcResult = await QueryLrcLibAsync(player, cancellationToken).ConfigureAwait(false);
            if (lrcResult.HasLyrics)
            {
                return lrcResult;
            }
        }
        catch (Exception)
        {
            // Ignore network glitch and fallback to NetEase
        }

        // 2. Try NetEase Cloud Music as second independent ground truth
        try
        {
            var neteaseResult = await QueryNetEaseAsync(player, cancellationToken).ConfigureAwait(false);
            if (neteaseResult.HasLyrics)
            {
                return neteaseResult;
            }
        }
        catch (Exception)
        {
            // Ignore
        }

        return new GroundTruthResult(false, "None", player.Title, player.Artist, null, Array.Empty<string>());
    }

    private async Task<GroundTruthResult> QueryLrcLibAsync(PlayerState player, CancellationToken cancellationToken)
    {
        var duration = (int)Math.Round(player.Duration);
        var exactUrl = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(player.Artist ?? "")}&track_name={Uri.EscapeDataString(player.Title ?? "")}&album_name={Uri.EscapeDataString(player.Album ?? "")}&duration={duration}";

        using var response = await _httpClient.GetAsync(exactUrl, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            var jsonStr = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;
            if (IsInstrumental(root))
            {
                return new GroundTruthResult(false, "LRCLIB (Instrumental)", player.Title, player.Artist, null, Array.Empty<string>());
            }

            var synced = root.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            var plain = root.TryGetProperty("plainLyrics", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            var lyrics = !string.IsNullOrWhiteSpace(synced) ? synced : plain;

            if (!string.IsNullOrWhiteSpace(lyrics))
            {
                var lines = ExtractCleanLines(lyrics);
                if (lines.Count > 0 && !MetadataMatching.IsRepetitiveVocalizationOnly(player.Title, lines))
                {
                    return new GroundTruthResult(true, "LRCLIB (Exact)", player.Title, player.Artist, lyrics, lines);
                }
            }
        }

        // Fallback: search
        var searchUrl = $"https://lrclib.net/api/search?artist_name={Uri.EscapeDataString(player.Artist ?? "")}&track_name={Uri.EscapeDataString(player.Title ?? "")}";
        using var searchResp = await _httpClient.GetAsync(searchUrl, cancellationToken).ConfigureAwait(false);
        if (searchResp.IsSuccessStatusCode)
        {
            var searchJson = await searchResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(searchJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                var first = doc.RootElement[0];
                if (IsInstrumental(first))
                {
                    return new GroundTruthResult(false, "LRCLIB (Instrumental)", player.Title, player.Artist, null, Array.Empty<string>());
                }

                var synced = first.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                var plain = first.TryGetProperty("plainLyrics", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                var lyrics = !string.IsNullOrWhiteSpace(synced) ? synced : plain;

                if (!string.IsNullOrWhiteSpace(lyrics))
                {
                    var lines = ExtractCleanLines(lyrics);
                    if (lines.Count > 0 && !MetadataMatching.IsRepetitiveVocalizationOnly(player.Title, lines))
                    {
                        return new GroundTruthResult(true, "LRCLIB (Search)", player.Title, player.Artist, lyrics, lines);
                    }
                }
            }
        }

        return new GroundTruthResult(false, "LRCLIB", player.Title, player.Artist, null, Array.Empty<string>());
    }

    private async Task<GroundTruthResult> QueryNetEaseAsync(PlayerState player, CancellationToken cancellationToken)
    {
        var keyword = $"{player.Title} {player.Artist}".Trim();
        var searchUrl = $"https://music.163.com/api/search/get/web?s={Uri.EscapeDataString(keyword)}&type=1&offset=0&total=true&limit=3";

        using var searchReq = new HttpRequestMessage(HttpMethod.Get, searchUrl);
        searchReq.Headers.Referrer = new Uri("https://music.163.com");
        using var searchResp = await _httpClient.SendAsync(searchReq, cancellationToken).ConfigureAwait(false);
        if (!searchResp.IsSuccessStatusCode)
        {
            return new GroundTruthResult(false, "NetEase", player.Title, player.Artist, null, Array.Empty<string>());
        }

        var searchJson = await searchResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(searchJson);
        if (!doc.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("songs", out var songs) ||
            songs.ValueKind != JsonValueKind.Array ||
            songs.GetArrayLength() == 0)
        {
            return new GroundTruthResult(false, "NetEase", player.Title, player.Artist, null, Array.Empty<string>());
        }

        long songId = 0;
        string? songName = null;
        string? artistName = null;

        foreach (var song in songs.EnumerateArray())
        {
            if (song.TryGetProperty("id", out var idProp))
            {
                songId = idProp.GetInt64();
                songName = song.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (song.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array && artists.GetArrayLength() > 0)
                {
                    artistName = artists[0].TryGetProperty("name", out var a) ? a.GetString() : null;
                }
                break;
            }
        }

        if (songId <= 0)
        {
            return new GroundTruthResult(false, "NetEase", player.Title, player.Artist, null, Array.Empty<string>());
        }

        var lyricUrl = $"https://music.163.com/api/song/lyric?id={songId}&lv=1&kv=1&tv=-1";
        using var lyricReq = new HttpRequestMessage(HttpMethod.Get, lyricUrl);
        lyricReq.Headers.Referrer = new Uri("https://music.163.com");
        using var lyricResp = await _httpClient.SendAsync(lyricReq, cancellationToken).ConfigureAwait(false);
        if (!lyricResp.IsSuccessStatusCode)
        {
            return new GroundTruthResult(false, "NetEase", songName, artistName, null, Array.Empty<string>());
        }

        var lyricJson = await lyricResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var lyricDoc = JsonDocument.Parse(lyricJson);
        if (lyricDoc.RootElement.TryGetProperty("lrc", out var lrcProp) &&
            lrcProp.TryGetProperty("lyric", out var lyricStrProp) &&
            lyricStrProp.ValueKind == JsonValueKind.String)
        {
            var rawLyric = lyricStrProp.GetString();
            if (!string.IsNullOrWhiteSpace(rawLyric) && !rawLyric.Contains("纯音乐，请欣赏"))
            {
                var lines = ExtractCleanLines(rawLyric);
                if (lines.Count > 0 && !MetadataMatching.IsRepetitiveVocalizationOnly(player.Title, lines))
                {
                    return new GroundTruthResult(true, "NetEase Music", songName ?? player.Title, artistName ?? player.Artist, rawLyric, lines);
                }
            }
        }

        return new GroundTruthResult(false, "NetEase", songName, artistName, null, Array.Empty<string>());
    }

    public static IReadOnlyList<string> ExtractCleanLines(string raw)
    {
        var results = new List<string>();
        foreach (var line in raw.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = LrcTimeTagRegex.Replace(line, "").Trim();
            if (string.IsNullOrWhiteSpace(clean))
            {
                continue;
            }

            // Skip metadata tags like [ti:], [ar:], [al:], 作词:, 作曲: etc.
            if (clean.StartsWith('[') && clean.EndsWith(']'))
            {
                continue;
            }

            results.Add(clean);
        }
        return results;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private static bool IsInstrumental(JsonElement result)
    {
        return result.TryGetProperty("instrumental", out var instrumental)
            && instrumental.ValueKind == JsonValueKind.True;
    }

    [GeneratedRegex(@"\[\d{2}:\d{2}(?:\.\d{1,3})?\]")]
    private static partial Regex MyRegex();
}

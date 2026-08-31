using System.Net;
using System.Net.Http;
using System.Text.Json;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Matching;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Parsing;

namespace AppleMusicLyrics.Infrastructure.Windows.External;

/// <summary>
/// Community lyrics database at lrclib.net. Chosen as the first external source because it needs no
/// account, no token and no reverse-engineered crypto: plain JSON in, LRC out.
///
/// It offers an exact endpoint that matches on title, artist, album and duration together, which is
/// far stronger identification than a search can give, so that is tried first and search is only the
/// fallback.
/// </summary>
public sealed class LrcLibLyricsProvider : IExternalLyricsProvider, IDisposable
{
    private const string BaseUrl = "https://lrclib.net/api";

    // The service asks clients to identify themselves rather than pretend to be a browser.
    private const string UserAgent = "AppleMusicLyrics (https://github.com/IzaiahZhang/AppleMusicLyrics)";

    private readonly HttpClient _httpClient;
    private readonly LrcLyricsParser _parser;
    private readonly Dictionary<string, LyricsDocument?> _cache = new(StringComparer.Ordinal);

    public LrcLibLyricsProvider(
        LrcLyricsParser? parser = null,
        double timeoutSeconds = 6.0,
        HttpMessageHandler? messageHandler = null)
    {
        _parser = parser ?? new LrcLyricsParser();
        _httpClient = messageHandler is null ? new HttpClient() : new HttpClient(messageHandler, disposeHandler: false);
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1.0, 30.0));
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
    }

    public string Name => "LRCLIB";

    public async Task<LyricsDocument?> FetchAsync(PlayerState player, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(player.Title) || string.IsNullOrWhiteSpace(player.Artist))
        {
            return null;
        }

        var key = BuildCacheKey(player);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var document = await FetchExactAsync(player, cancellationToken).ConfigureAwait(false)
            ?? await FetchBySearchAsync(player, cancellationToken).ConfigureAwait(false);

        // A completed lookup with no synchronized lyrics is definitive and may be cached. Network
        // failures throw before this point and are retried by LyricsRuntimeService instead.
        _cache[key] = document;
        return document;
    }

    private async Task<LyricsDocument?> FetchExactAsync(PlayerState player, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/get"
            + $"?artist_name={Uri.EscapeDataString(player.Artist ?? string.Empty)}"
            + $"&track_name={Uri.EscapeDataString(player.Title ?? string.Empty)}"
            + $"&album_name={Uri.EscapeDataString(player.Album ?? string.Empty)}"
            + $"&duration={(int)Math.Round(player.Duration)}";

        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"lrclib /get returned {(int)response.StatusCode}");
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var json = JsonDocument.Parse(payload);
        return ToDocument(json.RootElement);
    }

    private async Task<LyricsDocument?> FetchBySearchAsync(PlayerState player, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/search"
            + $"?artist_name={Uri.EscapeDataString(player.Artist ?? string.Empty)}"
            + $"&track_name={Uri.EscapeDataString(player.Title ?? string.Empty)}";

        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"lrclib /search returned {(int)response.StatusCode}");
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var json = JsonDocument.Parse(payload);
        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement? best = null;
        var bestScore = int.MinValue;
        var bestDelta = double.MaxValue;

        foreach (var result in json.RootElement.EnumerateArray())
        {
            if (!HasSyncedLyrics(result))
            {
                continue;
            }

            var score = ScoreResult(player, result);
            if (score == int.MinValue)
            {
                continue;
            }

            // Equal scores are settled by whichever length is closer, so the pick does not come
            // down to the order the service happened to return its rows in.
            var delta = DurationDelta(player, result);
            if (score > bestScore || (score == bestScore && delta < bestDelta))
            {
                bestScore = score;
                bestDelta = delta;
                best = result;
            }
        }

        // A result that agrees on neither title nor duration is not this song.
        return best is not null && bestScore > 0 ? ToDocument(best.Value) : null;
    }

    private static double DurationDelta(PlayerState player, JsonElement result)
    {
        return player.Duration > 0 &&
            result.TryGetProperty("duration", out var duration) &&
            duration.ValueKind is JsonValueKind.Number &&
            duration.TryGetDouble(out var candidateDuration) &&
            candidateDuration > 0
                ? Math.Abs(candidateDuration - player.Duration)
                : double.MaxValue;
    }

    private static int ScoreResult(PlayerState player, JsonElement result)
    {
        var titleScore = MetadataMatching.Score(player.Title, TryGetString(result, "trackName"), exact: 40, partial: 18);
        var artistScore = MetadataMatching.Score(player.Artist, TryGetString(result, "artistName"), exact: 30, partial: 14);
        var albumScore = MetadataMatching.Score(player.Album, TryGetString(result, "albumName"), exact: 20, partial: 8);

        if (titleScore == 0)
        {
            return int.MinValue;
        }

        var durationScore = 0;
        if (player.Duration > 0 &&
            result.TryGetProperty("duration", out var duration) &&
            duration.ValueKind is JsonValueKind.Number &&
            duration.TryGetDouble(out var candidateDuration) &&
            candidateDuration > 0)
        {
            var delta = Math.Abs(candidateDuration - player.Duration);

            // Different masters of one recording drift by a few seconds — Apple's own metadata does
            // it by four. Drift far past that and it is a live cut, an extended mix or a wholly
            // different take, whose timings would be useless however well the title matches.
            if (delta > 15.0)
            {
                return int.MinValue;
            }

            // Weighted to outrank an exact album match. Landing within a second of a four-minute
            // track says "this is the same recording" far more strongly than an album title does,
            // and album titles are the field most likely to disagree cosmetically — a real search
            // returned both "Rebel Heart" and "Rebel Heart (Limited Special Edition)" for one song.
            durationScore = delta switch
            {
                <= 1.0 => 40,
                <= 3.0 => 20,
                <= 6.0 => 8,
                _ => -15,
            };
        }

        return titleScore + artistScore + albumScore + durationScore;
    }

    private LyricsDocument? ToDocument(JsonElement result)
    {
        if (result.TryGetProperty("instrumental", out var instrumental) &&
            instrumental.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        var synced = TryGetString(result, "syncedLyrics");
        if (string.IsNullOrWhiteSpace(synced))
        {
            // Plain lyrics carry no timing, and this app has nothing to do with untimed text.
            return null;
        }

        double? duration = result.TryGetProperty("duration", out var durationElement)
            && durationElement.ValueKind == JsonValueKind.Number
            && durationElement.TryGetDouble(out var durationValue)
            && durationValue > 0
                ? durationValue
                : null;

        var id = result.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var idValue)
            ? idValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "unknown";

        var document = _parser.Parse(
            synced,
            sourceFile: $"lrclib:{id}",
            lyricsId: $"LRCLIB_{id}",
            durationSeconds: duration);

        return document.Lines.Count > 0 ? document : null;
    }

    private static bool HasSyncedLyrics(JsonElement result)
    {
        return result.TryGetProperty("syncedLyrics", out var synced)
            && synced.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(synced.GetString());
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string BuildCacheKey(PlayerState player)
    {
        return string.Join(
            "|",
            MetadataMatching.Normalize(player.Title),
            MetadataMatching.Normalize(player.Artist),
            MetadataMatching.Normalize(player.Album),
            ((int)Math.Round(player.Duration)).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

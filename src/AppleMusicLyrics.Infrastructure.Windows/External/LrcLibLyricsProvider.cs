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
    private const int MaxCacheEntries = 512;
    // Version 3 invalidates plain-only entries cached before synchronized search results were
    // preferred over an exact endpoint's stale plain text. LRCLIB entry 115521 for Ethel Cain's
    // "Family Tree (Intro)" is one real example whose plain text belongs partly to another song.
    private const int PersistentCacheVersion = 3;
    private static readonly TimeSpan PersistentMissLifetime = TimeSpan.FromDays(7);

    // The service asks clients to identify themselves rather than pretend to be a browser.
    private const string UserAgent = "AppleMusicLyrics (https://github.com/IzaiahZhang/AppleMusicLyrics)";

    private readonly HttpClient _httpClient;
    private readonly LrcLyricsParser _parser;
    private readonly string? _persistentCachePath;
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheInsertionOrder = new();
    private readonly object _cacheLock = new();

    public LrcLibLyricsProvider(
        LrcLyricsParser? parser = null,
        double timeoutSeconds = 6.0,
        HttpMessageHandler? messageHandler = null,
        string? persistentCachePath = null)
    {
        _parser = parser ?? new LrcLyricsParser();
        _persistentCachePath = string.IsNullOrWhiteSpace(persistentCachePath)
            ? null
            : persistentCachePath;
        _httpClient = messageHandler is null ? new HttpClient() : new HttpClient(messageHandler, disposeHandler: false);
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1.0, 30.0));
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        LoadPersistentCache();
    }

    public string Name => "LRCLIB";

    public async Task<LyricsDocument?> FetchAsync(PlayerState player, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(player.Title) || string.IsNullOrWhiteSpace(player.Artist))
        {
            return null;
        }

        var key = BuildCacheKey(player);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var cached)
                && (cached.Document is not null
                    || DateTimeOffset.UtcNow - cached.CachedAt <= PersistentMissLifetime))
            {
                return cached.Document;
            }
        }

        var exact = await FetchExactAsync(player, cancellationToken).ConfigureAwait(false);
        var document = exact;
        if (exact is null || IsProjectedPlainLyrics(exact))
        {
            // LRCLIB's exact endpoint can select an old plain-only revision even when search has a
            // correctly timed row for the same recording. Search in that case and prefer its
            // synchronized result, retaining the exact plain text only as a last resort.
            document = await FetchBySearchAsync(player, cancellationToken).ConfigureAwait(false)
                ?? exact;
        }

        // A completed lookup with no synchronized lyrics is definitive and may be cached. Network
        // failures throw before this point and are retried by LyricsRuntimeService instead.
        RememberCompletedLookup(key, document);
        return document;
    }

    private void RememberCompletedLookup(string key, LyricsDocument? document)
    {
        lock (_cacheLock)
        {
            if (!_cache.ContainsKey(key))
            {
                while (_cache.Count >= MaxCacheEntries && _cacheInsertionOrder.TryDequeue(out var oldest))
                {
                    _cache.Remove(oldest);
                }

                _cacheInsertionOrder.Enqueue(key);
            }

            _cache[key] = new CacheEntry(document, DateTimeOffset.UtcNow);
            SavePersistentCache();
        }
    }

    private void LoadPersistentCache()
    {
        if (_persistentCachePath is null || !File.Exists(_persistentCachePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_persistentCachePath);
            var persisted = JsonSerializer.Deserialize<PersistentCacheFile>(json);
            if (persisted is null || persisted.Version != PersistentCacheVersion)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var entry in persisted.Entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
                .Where(entry => entry.Document is not null || now - entry.CachedAt <= PersistentMissLifetime)
                .OrderByDescending(entry => entry.CachedAt)
                .Take(MaxCacheEntries)
                .Reverse())
            {
                _cache[entry.Key] = new CacheEntry(entry.Document, entry.CachedAt);
                _cacheInsertionOrder.Enqueue(entry.Key);
            }
        }
        catch (IOException)
        {
            // A cache is an optimization. A locked or partially written file must not prevent
            // online lyrics from being queried normally.
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (JsonException)
        {
        }
    }

    private void SavePersistentCache()
    {
        if (_persistentCachePath is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_persistentCachePath);
        var temporaryPath = $"{_persistentCachePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var persisted = new PersistentCacheFile(
                PersistentCacheVersion,
                _cache.Select(pair => new PersistentCacheEntry(
                    pair.Key,
                    pair.Value.Document,
                    pair.Value.CachedAt)).ToList());
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(persisted));
            File.Move(temporaryPath, _persistentCachePath, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
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
            if (!HasUsableLyrics(result))
            {
                continue;
            }

            var score = ScoreResult(player, result);
            if (score == int.MinValue)
            {
                continue;
            }

            // Prefer a synchronized row among otherwise comparable recordings without letting a
            // poor version match beat a substantially closer plain-lyrics row.
            if (HasSyncedLyrics(result))
            {
                score += 5;
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

        double? duration = result.TryGetProperty("duration", out var durationElement)
            && durationElement.ValueKind == JsonValueKind.Number
            && durationElement.TryGetDouble(out var durationValue)
            && durationValue > 0
                ? durationValue
                : null;

        var id = result.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var idValue)
            ? idValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "unknown";

        var synced = TryGetString(result, "syncedLyrics");
        var document = !string.IsNullOrWhiteSpace(synced)
            ? _parser.Parse(
                synced,
                sourceFile: $"lrclib:{id}",
                lyricsId: $"LRCLIB_{id}",
                durationSeconds: duration)
            : CreatePlainLyricsDocument(TryGetString(result, "plainLyrics"), id, duration);

        if (MetadataMatching.IsRepetitiveVocalizationOnly(
            TryGetString(result, "trackName"),
            document.Lines.Select(line => line.Text)))
        {
            return null;
        }

        return document.Lines.Count > 0 ? document : null;
    }

    private static LyricsDocument CreatePlainLyricsDocument(
        string? plainLyrics,
        string id,
        double? durationSeconds)
    {
        var texts = (plainLyrics ?? string.Empty)
            .Split('\n')
            .Select(line => line.Trim('\r', ' ', '\t'))
            .Where(line => line.Length > 0)
            .ToArray();
        if (texts.Length == 0)
        {
            return new LyricsDocument(
                $"LRCLIB_{id}",
                "plain",
                $"lrclib-plain:{id}",
                DateTimeOffset.UtcNow,
                []);
        }

        // Untimed text cannot be synchronized exactly. Evenly projecting it over the recording is
        // deliberately simple and deterministic: users get readable progressive lyrics while the
        // source label makes the approximation visible in diagnostics.
        var projectedDuration = durationSeconds is > 0
            ? durationSeconds.Value
            : texts.Length * 8.0;
        var secondsPerLine = projectedDuration / texts.Length;
        var lines = texts
            .Select((text, index) => new LyricsLine(
                Begin: index * secondsPerLine,
                End: (index + 1) * secondsPerLine,
                Text: text))
            .ToArray();

        return new LyricsDocument(
            LyricsId: $"LRCLIB_{id}",
            Status: "plain",
            SourceFile: $"lrclib-plain:{id}",
            UpdatedAt: DateTimeOffset.UtcNow,
            Lines: lines,
            DurationSeconds: durationSeconds);
    }

    private static bool HasSyncedLyrics(JsonElement result)
    {
        return result.TryGetProperty("syncedLyrics", out var synced)
            && synced.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(synced.GetString());
    }

    private static bool IsProjectedPlainLyrics(LyricsDocument document)
    {
        return document.SourceFile.StartsWith("lrclib-plain:", StringComparison.Ordinal);
    }

    private static bool HasUsableLyrics(JsonElement result)
    {
        return HasSyncedLyrics(result)
            || (result.TryGetProperty("plainLyrics", out var plain)
                && plain.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(plain.GetString()));
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

    private sealed record CacheEntry(LyricsDocument? Document, DateTimeOffset CachedAt);

    private sealed record PersistentCacheFile(int Version, List<PersistentCacheEntry> Entries);

    private sealed record PersistentCacheEntry(
        string Key,
        LyricsDocument? Document,
        DateTimeOffset CachedAt);
}

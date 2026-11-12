using System.Net.Http;
using System.Text.Json;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Matching;
using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Infrastructure.Windows.Catalog;

/// <summary>
/// Resolves the playing track to Apple catalog song ids through the public iTunes Search endpoint,
/// which needs no developer token, no account, and sends only the track's title and artist.
/// Results are returned as lyricsIds so callers can compare them directly against cached files.
/// </summary>
public sealed class ITunesCatalogSongResolver : ICatalogSongResolver, IDisposable
{
    private const int MaxCacheEntries = 2048;
    // Bounds cached-id verification to two lookup requests per storefront for each track.
    private const int LookupBatchSize = 100;
    internal const int MaxVerifiedLookupIds = 200;
    public const string DefaultStorefronts = "us,cn,jp,gb";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IReadOnlyList<string> _storefronts;
    private readonly string? _cachePath;
    private readonly Dictionary<string, string[]> _cache = new(StringComparer.Ordinal);
    // Session-only: new lyric files can appear at any time, so misses are never persisted.
    private readonly Dictionary<string, HashSet<long>> _verifiedMisses = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _cacheLoaded;

    public ITunesCatalogSongResolver(
        string? storefronts = null,
        string? cachePath = null,
        double timeoutSeconds = 3.0,
        HttpMessageHandler? messageHandler = null)
    {
        _storefronts = ParseStorefronts(storefronts);
        _cachePath = cachePath;
        _ownsHttpClient = true;
        _httpClient = messageHandler is null ? new HttpClient() : new HttpClient(messageHandler, disposeHandler: false);
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 0.5, 15.0));
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AppleMusicLyrics");
    }

    public async Task<IReadOnlyList<string>> ResolveLyricsIdCandidatesAsync(
        PlayerState player,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(player.Title) || _storefronts.Count == 0)
        {
            return Array.Empty<string>();
        }

        var key = BuildCacheKey(player);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            EnsureCacheLoaded();
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var resolved = await QueryStorefrontsAsync(player, cancellationToken).ConfigureAwait(false);
            if (resolved.Count == 0)
            {
                return Array.Empty<string>();
            }

            var lyricsIds = resolved.ToArray();
            RememberIds(key, lyricsIds);
            SaveCache();
            return lyricsIds;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> VerifyCachedLyricsIdsAsync(
        PlayerState player, IReadOnlyList<string> lyricsIds, CancellationToken cancellationToken = default)
    {
        var ids = lyricsIds.Select(CatalogLyricsId.ToCatalogId).OfType<long>().Distinct().ToArray();
        if (ids.Length == 0)
            return Array.Empty<string>();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCacheLoaded();
            // A search hit is not evidence that any of these particular ids was verified.
            // Keep lookup evidence separate and return only ids in the caller's requested set.
            var key = "verified|" + BuildCacheKey(player);
            if (_cache.TryGetValue(key, out var cached))
            {
                var requested = ids.ToHashSet();
                var hits = cached.Where(id => CatalogLyricsId.ToCatalogId(id) is { } value && requested.Contains(value)).ToArray();
                if (hits.Length > 0)
                    return hits;
            }

            // Ids already proven absent for this track are not looked up again, and each call
            // checks only the most recently cached files. Apple Music writes the playing song's
            // lyric file when it starts, so it sits at the front of the caller's newest-first list.
            _verifiedMisses.TryGetValue(key, out var misses);
            var pending = ids.Where(id => misses is null || !misses.Contains(id))
                .Take(MaxVerifiedLookupIds).ToArray();
            if (pending.Length == 0)
                return Array.Empty<string>();

            var failures = new List<Exception>();
            foreach (var batch in pending.Chunk(LookupBatchSize))
            {
                var batchComplete = true;
                foreach (var storefront in _storefronts)
                {
                    var url = "https://itunes.apple.com/lookup?id=" + string.Join(',', batch)
                        + "&country=" + Uri.EscapeDataString(storefront);
                    JsonDocument json;
                    try
                    {
                        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                        json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                    }
                    catch (Exception error) when (error is HttpRequestException or JsonException
                        || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                    {
                        // One storefront (for example a rate-limited one) must not hide another's answer.
                        failures.Add(error);
                        batchComplete = false;
                        continue;
                    }

                    using var _ = json;
                    if (!json.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                        continue;

                    var matches = results.EnumerateArray()
                        .Select(result => CreateCandidate(player, result))
                        .OfType<CatalogCandidate>()
                        .Where(candidate => candidate.IsStrongMatch
                            && candidate.DurationDelta <= LyricsMatchPolicy.DurationToleranceSeconds
                            && CatalogLyricsId.ToCatalogId(candidate.LyricsId) is { } id && batch.Contains(id))
                        .OrderByDescending(candidate => candidate.Score)
                        .ThenBy(candidate => candidate.DurationDelta)
                        .Select(candidate => candidate.LyricsId).Distinct().ToArray();
                    if (matches.Length == 0)
                        continue;

                    var proven = cached is null ? matches : matches.Concat(cached).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    RememberIds(key, proven);
                    // Verified ids also answer later searches, but a plain search never seeds
                    // the verification cache in the opposite direction.
                    var searchKey = BuildCacheKey(player);
                    _cache.TryGetValue(searchKey, out var searched);
                    RememberIds(searchKey, searched is null ? matches
                        : matches.Concat(searched).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
                    SaveCache();
                    return matches;
                }

                // Remember a miss only when every storefront answered for this batch, so a
                // transient failure is retried rather than mistaken for a definitive absence.
                if (batchComplete)
                    RememberVerifiedMisses(key, batch);
            }

            if (failures.Count > 0)
                throw new AggregateException("Apple catalog lookup could not verify cached lyric ids.", failures);
            return Array.Empty<string>();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<string>> QueryStorefrontsAsync(PlayerState player, CancellationToken cancellationToken)
    {
        var ranked = new List<CatalogCandidate>();

        foreach (var storefront in _storefronts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidates = await QueryStorefrontAsync(player, storefront, cancellationToken).ConfigureAwait(false);
            ranked.AddRange(candidates);

            // Keep ordinary search cheap. If these ids are not cached locally, the runtime
            // separately verifies its actual cached ids across storefronts through lookup.
            if (candidates.Any(candidate => candidate.IsStrongMatch))
            {
                break;
            }
        }

        return ranked
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.DurationDelta)
            .Select(candidate => candidate.LyricsId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<CatalogCandidate>> QueryStorefrontAsync(
        PlayerState player,
        string storefront,
        CancellationToken cancellationToken)
    {
        var term = string.Join(' ', new[] { player.Artist, player.Title }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

        var url = "https://itunes.apple.com/search"
            + $"?term={Uri.EscapeDataString(term)}"
            + "&entity=song&limit=25"
            + $"&country={Uri.EscapeDataString(storefront)}";

        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"iTunes Search returned {(int)response.StatusCode}");
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var json = JsonDocument.Parse(payload);
        if (!json.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var candidates = new List<CatalogCandidate>();
        foreach (var result in results.EnumerateArray())
        {
            var candidate = CreateCandidate(player, result);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    private static CatalogCandidate? CreateCandidate(PlayerState player, JsonElement result)
    {
        if (!result.TryGetProperty("trackId", out var trackIdElement) ||
            !trackIdElement.TryGetInt64(out var trackId))
        {
            return null;
        }

        var trackName = TryGetString(result, "trackName");
        var artistName = TryGetString(result, "artistName");
        var collectionName = TryGetString(result, "collectionName");
        var trackSeconds = result.TryGetProperty("trackTimeMillis", out var millis) && millis.TryGetInt64(out var value)
            ? value / 1000.0
            : 0.0;

        var titleScore = ScoreText(player.Title, trackName, exact: 40, partial: 18);
        if (titleScore == 0)
        {
            return null;
        }

        var artistScore = ScoreText(player.Artist, artistName, exact: 30, partial: 14);
        if (artistScore == 0)
        {
            return null;
        }
        var albumScore = ScoreText(player.Album, collectionName, exact: 20, partial: 8);

        var durationDelta = player.Duration > 0 && trackSeconds > 0
            ? Math.Abs(trackSeconds - player.Duration)
            : double.MaxValue;
        if (double.IsFinite(player.Duration) && player.Duration > 0
            && double.IsFinite(trackSeconds) && trackSeconds > 0
            && durationDelta > LyricsMatchPolicy.DurationToleranceSeconds)
        {
            // A shared title can name different recordings or an intro and a full song. Such
            // a catalog id must not verify a local candidate or stop later storefront lookups.
            return null;
        }
        var durationScore = durationDelta switch
        {
            <= 1.0 => 20,
            <= 3.0 => 12,
            <= 6.0 => 5,
            _ => 0,
        };

        return new CatalogCandidate(
            LyricsId: CatalogLyricsId.FromCatalogId(trackId),
            Score: titleScore + artistScore + albumScore + durationScore,
            DurationDelta: durationDelta,
            IsStrongMatch: titleScore == 40 && artistScore == 30);
    }

    private static int ScoreText(string? left, string? right, int exact, int partial)
    {
        return MetadataMatching.Score(left, right, exact, partial);
    }

    private static string NormalizeText(string? value)
    {
        return MetadataMatching.Normalize(value);
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
            "v2",
            NormalizeText(player.Title),
            NormalizeText(player.Artist),
            NormalizeText(player.Album),
            ((int)Math.Round(player.Duration)).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static IReadOnlyList<string> ParseStorefronts(string? storefronts)
    {
        return (string.IsNullOrWhiteSpace(storefronts) ? DefaultStorefronts : storefronts)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(storefront => storefront.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded)
        {
            return;
        }

        _cacheLoaded = true;
        if (string.IsNullOrWhiteSpace(_cachePath) || !File.Exists(_cachePath))
        {
            return;
        }

        try
        {
            var entries = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(_cachePath));
            if (entries is null)
            {
                return;
            }

            foreach (var (key, value) in entries)
            {
                _cache[key] = value;
            }

            while (_cache.Count > MaxCacheEntries)
            {
                _cache.Remove(_cache.Keys.First());
            }
        }
        catch (Exception)
        {
            // A corrupt cache is not worth reporting: it only costs one extra lookup.
        }
    }

    private void RememberVerifiedMisses(string key, IEnumerable<long> ids)
    {
        if (!_verifiedMisses.TryGetValue(key, out var misses))
        {
            if (_verifiedMisses.Count >= MaxCacheEntries)
                _verifiedMisses.Clear();
            _verifiedMisses[key] = misses = [];
        }
        misses.UnionWith(ids);
    }

    private void RememberIds(string key, string[] ids)
    {
        if (!_cache.ContainsKey(key) && _cache.Count >= MaxCacheEntries)
            _cache.Remove(_cache.Keys.First());
        _cache[key] = ids;
    }

    private void SaveCache()
    {
        if (string.IsNullOrWhiteSpace(_cachePath))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = _cachePath + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_cache));
                File.Move(temporaryPath, _cachePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        _gate.Dispose();
    }

    private sealed record CatalogCandidate(string LyricsId, int Score, double DurationDelta, bool IsStrongMatch);
}

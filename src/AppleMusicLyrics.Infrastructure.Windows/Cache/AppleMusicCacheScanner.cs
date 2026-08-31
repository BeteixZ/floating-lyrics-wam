using System.Text.Json;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Matching;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Parsing;

namespace AppleMusicLyrics.Infrastructure.Windows.Cache;

public sealed class AppleMusicCacheScanner : IPlayerMatchedLyricsProvider
{
    // Candidate discovery is deliberately generous. Apple reuses a single lyrics document across
    // several masters; LyricsMatchPolicy owns the shared threshold used by discovery and display.

    private readonly TtmlLyricsParser _parser;
    private readonly IReadOnlyList<string>? _fixedRoots;
    private readonly Dictionary<string, CachedDocument> _parseCache = new(StringComparer.OrdinalIgnoreCase);

    public AppleMusicCacheScanner(TtmlLyricsParser parser, IEnumerable<string>? roots = null)
    {
        _parser = parser;
        _fixedRoots = roots?.ToArray();
    }

    public async Task<LyricsDocument?> GetLatestLyricsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var roots = _fixedRoots ?? FindInetCacheRoots();
        if (roots.Count == 0)
        {
            return null;
        }

        var latestFile = FindLyricsFiles(roots)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        return latestFile is null
            ? null
            : await TryParseLyricsDocumentAsync(latestFile, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LyricsMatch>> FindCandidatesAsync(
        PlayerState player,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (player.Duration <= 0)
        {
            return Array.Empty<LyricsMatch>();
        }

        var roots = _fixedRoots ?? FindInetCacheRoots();
        if (roots.Count == 0)
        {
            return Array.Empty<LyricsMatch>();
        }

        var matches = new List<LyricsMatch>();
        foreach (var file in FindLyricsFiles(roots))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var document = await TryParseLyricsDocumentAsync(file, cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                continue;
            }

            var candidate = CreateMatch(player, document);
            if (candidate is not null)
            {
                matches.Add(candidate);
            }
        }

        return matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.DurationDelta)
            .ThenByDescending(match => match.Document.UpdatedAt)
            .ToArray();
    }

    public async Task<LyricsDocument?> FindBestLyricsAsync(
        PlayerState player,
        CancellationToken cancellationToken = default)
    {
        var candidates = await FindCandidatesAsync(player, cancellationToken).ConfigureAwait(false);
        return candidates.Count > 0 ? candidates[0].Document : null;
    }

    public IReadOnlyList<string> FindInetCacheRoots()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return Array.Empty<string>();
        }

        var packagesRoot = Path.Combine(localAppData, "Packages");
        if (!Directory.Exists(packagesRoot))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateDirectories(packagesRoot, "AppleInc.AppleMusicWin_*")
            .Select(directory => Path.Combine(directory, "AC", "INetCache"))
            .Where(Directory.Exists)
            .ToArray();
    }

    public IReadOnlyList<string> FindLyricsFiles(IEnumerable<string> roots)
    {
        var results = new List<string>();

        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                results.AddRange(Directory.EnumerateFiles(root, "ttmlLyrics*.json", SearchOption.AllDirectories));
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }

        return results;
    }

    public LyricsFileMetadata? LoadLyricsMetadata(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            string? lyricsId = root.TryGetProperty("lyricsId", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

            return new LyricsFileMetadata(
                Path: path,
                LyricsId: lyricsId,
                LastWriteTimeUtc: new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Candidates get re-scored on every poll for a few seconds after each track change, so parsing
    // is memoised on the file's last-write time; a rewritten cache file re-parses, an untouched one
    // costs a dictionary hit instead of an XML parse.
    private async Task<LyricsDocument?> TryParseLyricsDocumentAsync(string path, CancellationToken cancellationToken)
    {
        DateTimeOffset lastWriteTimeUtc;
        try
        {
            lastWriteTimeUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (_parseCache.TryGetValue(path, out var cached) && cached.LastWriteTimeUtc == lastWriteTimeUtc)
        {
            return cached.Document;
        }

        LyricsDocument? document;
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            document = _parser.ParseLyricsJson(json, path) with
            {
                UpdatedAt = lastWriteTimeUtc,
            };
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        _parseCache[path] = new CachedDocument(lastWriteTimeUtc, document);
        return document;
    }

    private static LyricsMatch? CreateMatch(PlayerState player, LyricsDocument document)
    {
        if (document.Lines.Count == 0)
        {
            return null;
        }

        var durationDelta = GetDurationDelta(player, document);
        var durationScore = durationDelta switch
        {
            <= 0.35 => 100,
            <= 0.75 => 92,
            <= 1.50 => 80,
            <= 3.00 => 60,
            <= 4.50 => 40,
            <= LyricsMatchPolicy.DurationToleranceSeconds => 25,
            _ => 0,
        };

        // Weak evidence — most songs never say their own title in the opening lines — so it breaks
        // ties without being able to outrank a whole duration bucket.
        var titleScore = ScoreTitle(player.Title, document);
        if (durationScore == 0 && titleScore == 0)
        {
            return null;
        }

        return new LyricsMatch(document, durationScore + titleScore, durationDelta);
    }

    public static double GetDurationDelta(PlayerState player, LyricsDocument document)
    {
        if (player.Duration <= 0)
        {
            return double.MaxValue;
        }

        var documentDuration = document.DurationSeconds
            ?? (document.Lines.Count > 0 ? document.Lines.Max(line => line.End) : 0.0);

        return documentDuration <= 0
            ? double.MaxValue
            : Math.Abs(documentDuration - player.Duration);
    }

    private static int ScoreTitle(string? title, LyricsDocument document)
    {
        var normalizedTitle = NormalizeText(title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return 0;
        }

        foreach (var line in document.Lines.Take(16))
        {
            var normalizedLine = NormalizeText(line.Text);
            if (normalizedLine.Contains(normalizedTitle, StringComparison.Ordinal))
            {
                return 8;
            }
        }

        return 0;
    }

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private sealed record CachedDocument(DateTimeOffset LastWriteTimeUtc, LyricsDocument Document);
}

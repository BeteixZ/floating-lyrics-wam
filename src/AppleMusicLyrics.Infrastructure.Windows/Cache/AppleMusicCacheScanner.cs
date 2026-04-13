using System.Text.Json;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Parsing;

namespace AppleMusicLyrics.Infrastructure.Windows.Cache;

public sealed class AppleMusicCacheScanner : IPlayerMatchedLyricsProvider
{
    private readonly TtmlLyricsParser _parser;
    private readonly IReadOnlyList<string>? _fixedRoots;
    private readonly HashSet<string> _seenLyricsIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTimeOffset _startedAt;
    private readonly double _recentFileGraceSeconds;

    public AppleMusicCacheScanner(
        TtmlLyricsParser parser,
        IEnumerable<string>? roots = null,
        DateTimeOffset? startedAt = null,
        double recentFileGraceSeconds = 2.0)
    {
        _parser = parser;
        _fixedRoots = roots?.ToArray();
        _startedAt = startedAt ?? DateTimeOffset.UtcNow;
        _recentFileGraceSeconds = recentFileGraceSeconds;
    }

    public async Task<LyricsDocument?> GetLatestLyricsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var roots = _fixedRoots ?? FindInetCacheRoots();
        if (roots.Count == 0)
        {
            return null;
        }

        var files = FindLyricsFiles(roots);
        var latestFile = GetLatestRecentFile(files, _startedAt, _seenLyricsIds)
            ?? files.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();

        if (latestFile is null || !File.Exists(latestFile))
        {
            return null;
        }

        var document = await TryParseLyricsDocumentAsync(latestFile, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(document.LyricsId))
        {
            _seenLyricsIds.Add(document.LyricsId);
        }

        return document;
    }

    public async Task<LyricsDocument?> FindBestLyricsAsync(PlayerState player, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (player.Duration <= 0)
        {
            return null;
        }

        var roots = _fixedRoots ?? FindInetCacheRoots();
        if (roots.Count == 0)
        {
            return null;
        }

        var files = FindLyricsFiles(roots);
        MatchCandidate? bestCandidate = null;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var document = await TryParseLyricsDocumentAsync(file, cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                continue;
            }

            var candidate = CreateMatchCandidate(player, document);
            if (candidate is null)
            {
                continue;
            }

            if (bestCandidate is null || candidate.IsBetterThan(bestCandidate))
            {
                bestCandidate = candidate;
            }
        }

        return bestCandidate?.Document;
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

    public string? GetLatestRecentFile(
        IEnumerable<string> files,
        DateTimeOffset startedAt,
        ISet<string> seenLyricsIds)
    {
        var threshold = startedAt.AddSeconds(-_recentFileGraceSeconds);

        return files
            .Select(LoadLyricsMetadata)
            .Where(metadata => metadata is not null)
            .Select(metadata => metadata!)
            .Where(metadata => metadata.LastWriteTimeUtc >= threshold)
            .Where(metadata => string.IsNullOrWhiteSpace(metadata.LyricsId) || !seenLyricsIds.Contains(metadata.LyricsId))
            .OrderByDescending(metadata => metadata.LastWriteTimeUtc)
            .Select(metadata => metadata.Path)
            .FirstOrDefault();
    }

    private async Task<LyricsDocument?> TryParseLyricsDocumentAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var document = _parser.ParseLyricsJson(json, path);

            return document with
            {
                UpdatedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
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
    }

    private static MatchCandidate? CreateMatchCandidate(PlayerState player, LyricsDocument document)
    {
        if (document.Lines.Count == 0)
        {
            return null;
        }

        var candidateDuration = document.DurationSeconds ?? document.Lines.Max(line => line.End);
        var durationDelta = Math.Abs(candidateDuration - player.Duration);
        var durationScore = durationDelta switch
        {
            <= 0.35 => 100,
            <= 0.75 => 92,
            <= 1.50 => 80,
            <= 3.00 => 60,
            <= 5.00 => 35,
            <= 8.00 => 15,
            _ => 0,
        };

        var titleScore = ScoreTitle(player.Title, document);
        if (durationScore == 0 && titleScore == 0)
        {
            return null;
        }

        return new MatchCandidate(document, durationScore + titleScore, durationDelta);
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
                return 20;
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

    private sealed record MatchCandidate(LyricsDocument Document, int Score, double DurationDelta)
    {
        public bool IsBetterThan(MatchCandidate other)
        {
            if (Score != other.Score)
            {
                return Score > other.Score;
            }

            var deltaComparison = DurationDelta.CompareTo(other.DurationDelta);
            if (deltaComparison != 0)
            {
                return deltaComparison < 0;
            }

            return Document.UpdatedAt > other.Document.UpdatedAt;
        }
    }
}

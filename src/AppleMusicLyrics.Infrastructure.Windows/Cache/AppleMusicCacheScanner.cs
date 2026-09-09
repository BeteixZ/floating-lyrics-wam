using System.Collections.Concurrent;
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
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, CachedDocument> _parseCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _indexLock = new();
    private string _indexedRootsKey = string.Empty;
    private string[] _indexedFiles = [];
    private DateTimeOffset _fileIndexExpiresAt = DateTimeOffset.MinValue;

    public AppleMusicCacheScanner(
        TtmlLyricsParser parser,
        IEnumerable<string>? roots = null,
        TimeProvider? timeProvider = null)
    {
        _parser = parser;
        _fixedRoots = roots?.ToArray();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public TimeSpan FileIndexTtl { get; init; } = TimeSpan.FromMilliseconds(750);

    public int IndexRefreshCount { get; private set; }

    public async Task<LyricsDocument?> GetLatestLyricsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var roots = _fixedRoots ?? FindInetCacheRoots();
        if (roots.Count == 0)
        {
            return null;
        }

        var latestFile = GetIndexedLyricsFiles(roots)
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
        foreach (var file in GetIndexedLyricsFiles(roots))
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
            .OrderByDescending(match => match.HasContentMatch)
            .ThenByDescending(match => match.Score)
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

    public void InvalidateFileIndex()
    {
        lock (_indexLock)
        {
            _fileIndexExpiresAt = DateTimeOffset.MinValue;
        }
    }

    private IReadOnlyList<string> GetIndexedLyricsFiles(IReadOnlyList<string> roots)
    {
        var normalizedRoots = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .OrderBy(root => root, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var rootsKey = string.Join("|", normalizedRoots);
        var now = _timeProvider.GetUtcNow();

        lock (_indexLock)
        {
            if (string.Equals(_indexedRootsKey, rootsKey, StringComparison.OrdinalIgnoreCase) &&
                now < _fileIndexExpiresAt)
            {
                return _indexedFiles;
            }

            _indexedFiles = FindLyricsFiles(normalizedRoots).ToArray();
            _indexedRootsKey = rootsKey;
            _fileIndexExpiresAt = now + (FileIndexTtl > TimeSpan.Zero ? FileIndexTtl : TimeSpan.Zero);
            IndexRefreshCount++;

            var liveFiles = _indexedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stalePath in _parseCache.Keys.Where(path => !liveFiles.Contains(path)).ToArray())
            {
                _parseCache.TryRemove(stalePath, out _);
            }

            return _indexedFiles;
        }
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

        // Content is supporting evidence, never permission to ignore a recording-length mismatch.
        // This accuracy-first boundary prevents a common title word from turning another song with
        // a long instrumental difference into an apparently close candidate.
        if (durationDelta > LyricsMatchPolicy.DurationToleranceSeconds)
        {
            return null;
        }

        var titleEvidence = MetadataMatching.GetTitleEvidence(
            player.Title,
            document.Lines.Select(line => line.Text));

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

        var titleScore = titleEvidence switch
        {
            TitleEvidenceStrength.Strong => 60,
            TitleEvidenceStrength.Weak => 10,
            _ => 0,
        };
        if (durationScore == 0 && titleScore == 0)
        {
            return null;
        }

        return new LyricsMatch(
            document,
            durationScore + titleScore,
            durationDelta,
            HasContentMatch: titleEvidence == TitleEvidenceStrength.Strong,
            TitleEvidence: titleEvidence);
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

    private sealed record CachedDocument(DateTimeOffset LastWriteTimeUtc, LyricsDocument Document);
}

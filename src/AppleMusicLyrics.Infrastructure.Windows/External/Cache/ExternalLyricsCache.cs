using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppleMusicLyrics.Core.Matching;
using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Infrastructure.Windows.External;

public sealed record ExternalLyricsCacheUsage(long SizeBytes, int EntryCount, bool IsAvailable);

public sealed record ExternalLyricsCacheClearResult(bool Success, ExternalLyricsCacheUsage RemainingUsage);

/// <summary>A shared disk quota for completed external lyric documents. Disabled caches skip automatic reads and writes.</summary>
public sealed class ExternalLyricsCache
{
    public const int DefaultMaxMegabytes = 64;
    public const int MinimumMaxMegabytes = 1;
    public const int MaximumMaxMegabytes = 1024;
    private const int Version = 1;
    private const long Megabyte = 1024L * 1024L;
    private const long MaximumEntryBytes = 16L * Megabyte;
    private static readonly Regex EntryName = new(@"\Av1-[0-9a-fA-F]{64}\.json\z", RegexOptions.CultureInvariant);
    private static readonly Regex TemporaryName = new(@"\Av1-[0-9a-fA-F]{64}\.json\.[0-9a-fA-F]{32}\.tmp\z", RegexOptions.CultureInvariant);
    private readonly string _directory;
    private readonly object _gate = new();
    private bool _enabled;
    private int _maxMegabytes = DefaultMaxMegabytes;
    private long _generation;

    public ExternalLyricsCache(string directoryPath)
    {
        _directory = Path.GetFullPath(directoryPath);
    }

    public bool Enabled { get { lock (_gate) return _enabled; } }
    public int MaxMegabytes { get { lock (_gate) return _maxMegabytes; } }
    public long Generation { get { lock (_gate) return _generation; } }

    /// <summary>Counts this cache's top-level entries and temporary bytes, even when retention is disabled.</summary>
    public ExternalLyricsCacheUsage GetUsage()
    {
        lock (_gate) return GetUsageCore();
    }

    /// <summary>Removes only recognized cache files, retaining the directory and the current configuration.</summary>
    public ExternalLyricsCacheClearResult Clear()
    {
        lock (_gate)
        {
            // In-flight provider calls can still return usable lyrics but cannot undo this clear.
            unchecked { _generation++; }
            var success = true;
            try
            {
                foreach (var entry in GetManagedFiles())
                {
                    try { entry.Delete(); }
                    catch (Exception error) when (IsCacheFailure(error)) { success = false; }
                }
            }
            catch (DirectoryNotFoundException) { success = !File.Exists(_directory); }
            catch (Exception error) when (IsCacheFailure(error)) { success = false; }

            var remaining = GetUsageCore();
            return new ExternalLyricsCacheClearResult(success && remaining.IsAvailable
                && remaining.SizeBytes == 0 && remaining.EntryCount == 0, remaining);
        }
    }

    public void Configure(bool enabled, int maxMegabytes)
    {
        lock (_gate)
        {
            _enabled = enabled;
            _maxMegabytes = Math.Clamp(maxMegabytes, MinimumMaxMegabytes, MaximumMaxMegabytes);
            if (_enabled)
            {
                TryEnforceLimit();
            }
        }
    }

    public bool TryGet(string sourceName, PlayerState player, out LyricsDocument? document)
    {
        document = null;
        lock (_gate)
        {
            if (!_enabled) return false;
            var key = BuildKey(sourceName, player);
            var path = GetPath(key);
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length == 0 || stream.Length > Math.Min(_maxMegabytes * Megabyte, MaximumEntryBytes))
                {
                    return false;
                }
                var entry = JsonSerializer.Deserialize<CacheEntry>(stream);
                if (entry is null || entry.Version != Version || entry.Key != key || !IsUsable(entry.Document))
                {
                    return false;
                }
                document = entry.Document;
            }
            catch (Exception error) when (IsCacheFailure(error))
            {
                return false;
            }

            // File timestamps give all sources one least-recently-used order across restarts.
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
            catch (Exception error) when (IsCacheFailure(error)) { }
            TryEnforceLimit();
            return true;
        }
    }

    public void Store(string sourceName, PlayerState player, LyricsDocument document, long? expectedGeneration = null)
    {
        lock (_gate)
        {
            if (!_enabled || (expectedGeneration is { } expected && expected != _generation) || !IsUsable(document)) return;
            var key = BuildKey(sourceName, player);
            var path = GetPath(key);
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(_directory);
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, new CacheEntry(Version, key, document));
                    if (stream.Length > Math.Min(_maxMegabytes * Megabyte, MaximumEntryBytes)) return;
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, path, overwrite: true);
                TryEnforceLimit();
            }
            catch (Exception error) when (IsCacheFailure(error))
            {
                // A read-only, full, or damaged cache must never turn usable lyrics into failure.
            }
            finally
            {
                try { File.Delete(temporaryPath); }
                catch (Exception error) when (IsCacheFailure(error)) { }
            }
        }
    }

    private void TryEnforceLimit()
    {
        try
        {
            var entries = GetManagedFiles()
                .OrderBy(entry => entry.LastWriteTimeUtc)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal)
                .ToArray();
            var totalBytes = entries.Sum(entry => entry.Length);
            var limit = _maxMegabytes * Megabyte;
            foreach (var entry in entries)
            {
                if (totalBytes <= limit) break;
                try
                {
                    var bytes = entry.Length;
                    entry.Delete();
                    totalBytes -= bytes;
                }
                catch (Exception error) when (IsCacheFailure(error)) { }
            }
        }
        catch (Exception error) when (IsCacheFailure(error)) { }
    }

    private ExternalLyricsCacheUsage GetUsageCore()
    {
        FileInfo[] entries;
        try { entries = GetManagedFiles(); }
        catch (DirectoryNotFoundException) { return new(0, 0, !File.Exists(_directory)); }
        catch (Exception error) when (IsCacheFailure(error)) { return new(0, 0, false); }

        long bytes = 0;
        var count = 0;
        var available = true;
        foreach (var entry in entries)
        {
            try
            {
                bytes += entry.Length;
                if (EntryName.IsMatch(entry.Name)) count++;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception error) when (IsCacheFailure(error)) { available = false; }
        }
        return new(bytes, count, available);
    }

    private FileInfo[] GetManagedFiles() => new DirectoryInfo(_directory)
        .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
        .Where(entry => (EntryName.IsMatch(entry.Name) || TemporaryName.IsMatch(entry.Name))
            && string.Equals(Path.GetDirectoryName(Path.GetFullPath(entry.FullName)),
                Path.TrimEndingDirectorySeparator(_directory), StringComparison.OrdinalIgnoreCase))
        .ToArray();

    private string GetPath(string key) => Path.Combine(_directory,
        "v1-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");

    private static string BuildKey(string sourceName, PlayerState player) => JsonSerializer.Serialize(new TrackKey(
        sourceName.Trim(), MetadataMatching.Normalize(player.Title), MetadataMatching.Normalize(player.Artist),
        MetadataMatching.Normalize(player.Album), player.Duration.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));

    private static bool IsUsable(LyricsDocument? document)
    {
        return document is { SourceFile: not null, Lines.Count: > 0 }
            && (document.DurationSeconds is null || double.IsFinite(document.DurationSeconds.Value))
            && double.IsFinite(document.LeadingSilenceSeconds) && double.IsFinite(document.NativeOffsetSeconds)
            && document.Lines.All(line => line is not null && line.Text is not null
                && double.IsFinite(line.Begin) && double.IsFinite(line.End) && line.Begin >= 0 && line.End >= line.Begin
                && (line.Words is null || line.Words.All(word => word is not null && word.Text is not null
                    && double.IsFinite(word.Begin) && double.IsFinite(word.End) && word.Begin >= 0 && word.End >= word.Begin)));
    }

    private static bool IsCacheFailure(Exception error) => error is IOException or UnauthorizedAccessException
        or JsonException or NotSupportedException or ArgumentException or System.Security.SecurityException;

    private sealed record CacheEntry(int Version, string Key, LyricsDocument Document);
    private sealed record TrackKey(string Source, string Title, string Artist, string Album, string Duration);
}

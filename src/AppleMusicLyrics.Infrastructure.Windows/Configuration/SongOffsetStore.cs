using System.Text.Json;
using AppleMusicLyrics.Core.Abstractions;

namespace AppleMusicLyrics.Infrastructure.Windows.Configuration;

public sealed class SongOffsetStore : ISongOffsetStore
{
    private const int DefaultCapacity = 500;
    private readonly string _path;
    private readonly int _capacity;
    private readonly object _gate = new();
    private Dictionary<string, SongOffsetEntry>? _entries;

    public SongOffsetStore(string path, int capacity = DefaultCapacity)
    {
        _path = path;
        _capacity = Math.Max(1, capacity);
    }

    public double? TryGetOffset(string? artist, string? title)
    {
        var key = BuildKey(artist, title);
        if (key is null)
        {
            return null;
        }

        lock (_gate)
        {
            var entries = EnsureLoaded();
            if (!entries.TryGetValue(key, out var entry))
            {
                return null;
            }

            entry.LastUsedTicks = DateTimeOffset.UtcNow.UtcTicks;
            return entry.Offset;
        }
    }

    public void RememberOffset(string? artist, string? title, double offsetSeconds)
    {
        var key = BuildKey(artist, title);
        if (key is null || !double.IsFinite(offsetSeconds))
        {
            return;
        }

        lock (_gate)
        {
            var entries = EnsureLoaded();
            entries[key] = new SongOffsetEntry
            {
                Offset = offsetSeconds,
                LastUsedTicks = DateTimeOffset.UtcNow.UtcTicks,
            };
            while (entries.Count > _capacity)
            {
                var oldest = entries.OrderBy(pair => pair.Value.LastUsedTicks).First().Key;
                entries.Remove(oldest);
            }

            Save(entries);
        }
    }

    private Dictionary<string, SongOffsetEntry> EnsureLoaded()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        _entries = Load();
        return _entries;
    }

    private Dictionary<string, SongOffsetEntry> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new Dictionary<string, SongOffsetEntry>(StringComparer.Ordinal);
            }

            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, SongOffsetEntry>>(json);
            return loaded is null
                ? new Dictionary<string, SongOffsetEntry>(StringComparer.Ordinal)
                : new Dictionary<string, SongOffsetEntry>(loaded, StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing, locked, or hand-corrupted file must not break playback; start empty.
            return new Dictionary<string, SongOffsetEntry>(StringComparer.Ordinal);
        }
    }

    private void Save(Dictionary<string, SongOffsetEntry> entries)
    {
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Persistence is best-effort; the in-memory calibration still applies this session.
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string? BuildKey(string? artist, string? title)
    {
        var normalizedArtist = Normalize(artist);
        var normalizedTitle = Normalize(title);
        if (normalizedArtist.Length == 0 && normalizedTitle.Length == 0)
        {
            return null;
        }

        return normalizedArtist + "|" + normalizedTitle;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
    }

    private sealed class SongOffsetEntry
    {
        public double Offset { get; set; }

        public long LastUsedTicks { get; set; }
    }
}
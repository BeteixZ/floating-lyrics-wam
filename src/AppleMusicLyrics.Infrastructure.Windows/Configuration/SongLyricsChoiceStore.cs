using System.Security.Cryptography;
using System.Text.Json;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Infrastructure.Windows.Configuration;

/// <summary>Stores explicitly selected lyrics independently of the automatic provider cache.</summary>
public sealed class SongLyricsChoiceStore(string directory) : ISongLyricsChoiceStore
{
    // SMTC can refine its duration after publishing the metadata. Keep nearby reports together
    // while separating recordings with the same title, artist and album but different lengths.
    private const double DurationToleranceSeconds = 1.0;
    private readonly object _gate = new();

    public LyricsChoice? Load(PlayerState player)
    {
        lock (_gate)
        {
            try
            {
                return FindRecording(ReadEntries(GetPath(player)), player)?.Choice;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public void Save(PlayerState player, LyricsChoice? choice)
    {
        lock (_gate)
        {
            var path = GetPath(player);
            var entries = ReadEntries(path);
            // Replace or clear exactly the entry Load would return. Another recording saved within
            // the tolerance of this report (180.0 and 181.6 s, reported as 180.7 s) keeps its choice.
            var existing = FindRecording(entries, player);
            if (existing is not null)
                entries.Remove(existing);
            if (choice is not null)
                entries.Add(new(player.Duration, choice));
            else if (existing is null)
                return;

            if (entries.Count == 0)
            {
                File.Delete(path);
                return;
            }

            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Directory.CreateDirectory(directory);
            try
            {
                using (var stream = File.Open(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    JsonSerializer.Serialize(stream, entries);
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporaryPath); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private string GetPath(PlayerState player)
    {
        string?[] metadata = [player.Title, player.Artist, player.Album, player.SourceAppId];
        var key = JsonSerializer.SerializeToUtf8Bytes(metadata.Select(value => value?.Trim().ToUpperInvariant() ?? string.Empty));
        return Path.Combine(directory, Convert.ToHexStringLower(SHA256.HashData(key)) + ".json");
    }

    private static SavedChoice? FindRecording(List<SavedChoice> entries, PlayerState player) =>
        entries.Where(entry => Math.Abs(entry.Duration - player.Duration) <= DurationToleranceSeconds)
            .MinBy(entry => Math.Abs(entry.Duration - player.Duration));

    private static List<SavedChoice> ReadEntries(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var entries = JsonSerializer.Deserialize<List<SavedChoice>>(stream) ?? [];
            entries.RemoveAll(entry => entry?.Choice?.Document is not { Lines.Count: > 0, SourceFile: not null } document
                || document.Lines.Any(line => line?.Text is null));
            return entries;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return [];
        }
    }

    private sealed record SavedChoice(double Duration, LyricsChoice Choice);
}

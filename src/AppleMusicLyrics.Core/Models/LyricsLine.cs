namespace AppleMusicLyrics.Core.Models;

public sealed record LyricsLine(
    double Begin,
    double End,
    string Text,
    string? SongPart = null,
    IReadOnlyList<LyricWord>? Words = null,
    string? Translation = null);

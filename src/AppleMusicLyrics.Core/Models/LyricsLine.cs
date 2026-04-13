namespace AppleMusicLyrics.Core.Models;

public sealed record LyricsLine(
    double Begin,
    double End,
    string Text,
    string? SongPart = null);

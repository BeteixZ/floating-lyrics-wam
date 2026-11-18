namespace AppleMusicLyrics.Core.Models;

public sealed record LyricsLine(
    double Begin,
    double End,
    string Text,
    string? SongPart = null,
    IReadOnlyList<LyricWord>? Words = null,
    string? Translation = null,
    string? Romanization = null)
{
    public string? VocalistId { get; init; }
    public IReadOnlyList<LyricsLine>? BackgroundVocals { get; init; }
}

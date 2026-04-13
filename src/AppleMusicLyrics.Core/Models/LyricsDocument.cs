namespace AppleMusicLyrics.Core.Models;

public sealed record LyricsDocument(
    string? LyricsId,
    string? Status,
    string SourceFile,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<LyricsLine> Lines,
    double? DurationSeconds = null,
    double LeadingSilenceSeconds = 0.0,
    double NativeOffsetSeconds = 0.0);

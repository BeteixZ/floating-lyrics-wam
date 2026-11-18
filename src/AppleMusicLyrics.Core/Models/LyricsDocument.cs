namespace AppleMusicLyrics.Core.Models;

public sealed record LyricsDocument(
    string? LyricsId,
    string? Status,
    string SourceFile,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<LyricsLine> Lines,
    double? DurationSeconds = null,
    double LeadingSilenceSeconds = 0.0,
    double NativeOffsetSeconds = 0.0)
{
    public long? CatalogTrackId { get; init; }

    public bool HasMultipleVocalists() => Lines.Select(line => line.VocalistId)
        .Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).Skip(1).Any();
}

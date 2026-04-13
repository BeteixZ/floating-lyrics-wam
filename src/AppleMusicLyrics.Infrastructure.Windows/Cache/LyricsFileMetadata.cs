namespace AppleMusicLyrics.Infrastructure.Windows.Cache;

public sealed record LyricsFileMetadata(
    string Path,
    string? LyricsId,
    DateTimeOffset LastWriteTimeUtc);

using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Core.Abstractions;

/// <summary>
/// Resolves the playing track to the lyricsIds it could legitimately own, so a caller holding
/// several cached lyrics files can tell which one is actually this song.
/// </summary>
public interface ICatalogSongResolver
{
    /// <summary>
    /// Candidate lyricsIds for <paramref name="player"/>, best first. An empty list means the
    /// lookup completed but found no confident catalog match. Transient network failures may throw;
    /// callers are responsible for bounded retry and must preserve cancellation.
    /// </summary>
    Task<IReadOnlyList<string>> ResolveLyricsIdCandidatesAsync(
        PlayerState player,
        CancellationToken cancellationToken = default);
}

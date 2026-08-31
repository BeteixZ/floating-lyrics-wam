using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Core.Abstractions;

public interface IPlayerMatchedLyricsProvider : ILyricsDocumentProvider
{
    /// <summary>
    /// Every cached lyrics document that could belong to <paramref name="player"/>, best first.
    /// Returns all of them rather than a single winner so the caller can notice when the local
    /// signals cannot separate two songs and reach for a stronger tie-breaker.
    /// </summary>
    Task<IReadOnlyList<LyricsMatch>> FindCandidatesAsync(
        PlayerState player,
        CancellationToken cancellationToken = default);
}

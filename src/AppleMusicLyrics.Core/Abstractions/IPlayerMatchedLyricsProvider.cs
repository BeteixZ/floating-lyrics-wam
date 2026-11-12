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

    /// <summary>
    /// Finds an independently verified Apple catalog id, including documents whose lyric timeline
    /// ends before the recording does. Implementations without id lookup may return null.
    /// </summary>
    Task<LyricsDocument?> FindByLyricsIdsAsync(
        IReadOnlyList<string> lyricsIds,
        CancellationToken cancellationToken = default) => Task.FromResult<LyricsDocument?>(null);

    Task<IReadOnlyList<string>> GetCachedLyricsIdsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}

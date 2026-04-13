using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Core.Abstractions;

public interface IPlayerMatchedLyricsProvider : ILyricsDocumentProvider
{
    Task<LyricsDocument?> FindBestLyricsAsync(PlayerState player, CancellationToken cancellationToken = default);
}

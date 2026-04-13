using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Core.Abstractions;

public interface ILyricsDocumentProvider
{
    Task<LyricsDocument?> GetLatestLyricsAsync(CancellationToken cancellationToken = default);
}

using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Core.Abstractions;

public interface IPlayerSessionProvider
{
    Task<PlayerState?> GetCurrentPlayerStateAsync(CancellationToken cancellationToken = default);
}

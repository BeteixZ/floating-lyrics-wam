using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Core.Abstractions;

/// <summary>
/// Fetches lyrics for a track from somewhere other than Apple's own on-disk cache, for the songs
/// that cache simply does not have.
/// </summary>
public interface IExternalLyricsProvider
{
    /// <summary>Shown in diagnostics so it is obvious where a displayed document came from.</summary>
    string Name { get; }

    /// <summary>
    /// Returns timed lyrics for <paramref name="player"/>, or null when this source completed its
    /// lookup but has no synchronized lyrics. Transient failures may throw so the runtime can apply
    /// bounded retry; cancellation must always be propagated.
    /// </summary>
    Task<LyricsDocument?> FetchAsync(PlayerState player, CancellationToken cancellationToken = default);
}

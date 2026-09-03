namespace AppleMusicLyrics.Core.Models;

/// <summary>
/// A cached lyrics file that could belong to the playing track, together with how well it fit.
/// <paramref name="DurationDelta"/> is the gap in seconds between the document's own idea of the
/// song length and the player's, and is only ever a hint: Apple reuses one lyrics document across
/// several masters of the same song, so the two can legitimately disagree by several seconds.
/// </summary>
public sealed record LyricsMatch(LyricsDocument Document, int Score, double DurationDelta, bool HasContentMatch = false);

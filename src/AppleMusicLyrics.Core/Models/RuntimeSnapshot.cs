namespace AppleMusicLyrics.Core.Models;

public sealed record RuntimeSnapshot(
    LyricsDocument? Document,
    PlayerState? Player,
    ActiveLyricState ActiveLyric,
    double? RawPositionSeconds = null,
    double? EstimatedPositionSeconds = null);

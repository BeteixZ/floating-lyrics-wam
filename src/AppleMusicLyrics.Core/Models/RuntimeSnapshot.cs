namespace AppleMusicLyrics.Core.Models;

public sealed record RuntimeSnapshot(
    LyricsDocument? Document,
    PlayerState? Player,
    ActiveLyricState ActiveLyric,
    LyricsResolution Resolution,
    double? RawPositionSeconds = null,
    double? EstimatedPositionSeconds = null,
    ExternalLyricsDiagnostics? ExternalDiagnostics = null)
{
    public int TrackGeneration { get; init; }
    public bool IsManualSelection { get; init; }
    public bool HasRememberedLyrics { get; init; }
    public bool CanManageLyrics => Player is { Duration: > 0 } player && double.IsFinite(player.Duration)
        && !string.IsNullOrWhiteSpace(player.Title);
}

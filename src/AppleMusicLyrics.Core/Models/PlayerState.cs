namespace AppleMusicLyrics.Core.Models;

/// <param name="PositionAgeSeconds">
/// How long before this state was read the player published <paramref name="Position"/>, when the
/// session reports it. Apple Music publishes whole seconds from a free-running timer, so this
/// timestamp is what lets the playback clock place each report on the real timeline.
/// </param>
public sealed record PlayerState(
    string? Title,
    string? Artist,
    string? Album,
    double Position,
    double Duration,
    bool Playing,
    string? SourceAppId = null,
    double? PositionAgeSeconds = null);

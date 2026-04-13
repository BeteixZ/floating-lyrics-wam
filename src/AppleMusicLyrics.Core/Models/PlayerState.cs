namespace AppleMusicLyrics.Core.Models;

public sealed record PlayerState(
    string? Title,
    string? Artist,
    string? Album,
    double Position,
    double Duration,
    bool Playing,
    string? SourceAppId = null);

namespace AppleMusicLyrics.Core.Abstractions;

/// <summary>
/// Persists the hand-tuned lyric offset per song so the same track keeps its calibration
/// across restarts. Keys are derived from artist and title; implementations normalize them.
/// </summary>
public interface ISongOffsetStore
{
    double? TryGetOffset(string? artist, string? title);

    void RememberOffset(string? artist, string? title, double offsetSeconds);
}

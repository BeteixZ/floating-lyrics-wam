namespace AppleMusicLyrics.Core.Models;

/// <summary>A text fragment with absolute begin/end times on the lyric timeline, in seconds.</summary>
public sealed record LyricWord(double Begin, double End, string Text);

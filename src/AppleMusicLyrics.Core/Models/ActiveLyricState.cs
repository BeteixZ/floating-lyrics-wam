namespace AppleMusicLyrics.Core.Models;

public sealed record ActiveLyricState(
    int? CurrentIndex,
    LyricsLine? PreviousLine,
    LyricsLine? CurrentLine,
    LyricsLine? NextLine)
{
    public LyricsLine? DuetLine { get; init; }
}

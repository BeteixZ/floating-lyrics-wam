namespace AppleMusicLyrics.Core.Models;

public sealed record LyricsChoice(LyricsDocument Document, LyricsResolutionSource Source, string? ProviderName = null);

public sealed record LyricsChoiceResults(IReadOnlyList<LyricsChoice> Choices, int FailedSources);

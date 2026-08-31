using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Core.Matching;

/// <summary>
/// Shared thresholds for discovering lyric candidates and deciding whether duration alone is
/// strong enough evidence to display one. Candidate discovery stays deliberately broad; display
/// confidence is intentionally strict.
/// </summary>
public static class LyricsMatchPolicy
{
    public const double DurationToleranceSeconds = 6.0;

    public const double ConfidentDurationDeltaSeconds = 1.0;

    public static bool IsPlausible(LyricsMatch candidate)
    {
        return candidate.DurationDelta <= DurationToleranceSeconds;
    }

    public static bool IsConfidentSingle(IReadOnlyList<LyricsMatch> plausibleCandidates)
    {
        return plausibleCandidates.Count == 1
            && plausibleCandidates[0].DurationDelta <= ConfidentDurationDeltaSeconds;
    }
}

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

    /// <summary>
    /// A single candidate within this window is "probably right" — close enough to display as
    /// medium confidence without waiting for catalog verification. Apple reuses one lyrics file
    /// across several masters of the same song, so 2–3 s deltas are normal.
    /// </summary>
    public const double MediumDurationDeltaSeconds = 3.0;

    /// <summary>
    /// When multiple plausible candidates exist, the top scorer must lead the runner-up by at
    /// least this many points before we declare a clear winner without catalog confirmation.
    /// </summary>
    public const int ClearWinnerScoreMargin = 20;

    public static bool IsPlausible(LyricsMatch candidate)
    {
        return candidate.DurationDelta <= DurationToleranceSeconds;
    }

    public static bool IsConfidentSingle(IReadOnlyList<LyricsMatch> plausibleCandidates)
    {
        return plausibleCandidates.Count == 1
            && plausibleCandidates[0].DurationDelta <= ConfidentDurationDeltaSeconds;
    }

    /// <summary>
    /// A single candidate within the medium duration window that has content verification
    /// (e.g. lyrics text contains the title). Pure duration alone without content or catalog
    /// verification is low-confidence evidence, not medium.
    /// </summary>
    public static bool IsMediumConfidenceSingle(IReadOnlyList<LyricsMatch> plausibleCandidates)
    {
        return plausibleCandidates.Count == 1
            && plausibleCandidates[0].HasContentMatch
            && plausibleCandidates[0].DurationDelta <= MediumDurationDeltaSeconds;
    }

    /// <summary>
    /// Among multiple plausible candidates, the highest scorer leads the next one by a
    /// significant margin and is within the medium duration window.
    /// </summary>
    public static bool HasClearWinner(IReadOnlyList<LyricsMatch> plausibleCandidates)
    {
        if (plausibleCandidates.Count < 2)
        {
            return false;
        }

        var best = plausibleCandidates[0];
        var second = plausibleCandidates[1];
        return best.Score - second.Score >= ClearWinnerScoreMargin
            && best.DurationDelta <= MediumDurationDeltaSeconds;
    }
}

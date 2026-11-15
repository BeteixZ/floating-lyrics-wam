using System.Text;
using AppleMusicLyrics.Core.Matching;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Parsing;
using AppleMusicLyrics.Core.Sync;

namespace AppleMusicLyrics.Application.Services;

/// <summary>
/// Fills missing features on an authoritative native timeline. An external document is never
/// adopted as the primary document, and a failed alignment leaves the original snapshot intact.
/// </summary>
/// <remarks>
/// A line is paired by its lyric text, its order, and when it starts. Line ends are not compared:
/// LRC-derived sources end each line where the next begins and QRC sources hold the last syllable,
/// so their ends routinely differ from Apple's by more than a second even when the onsets agree to
/// a few hundredths. A source whose whole timeline is consistently shifted is first re-registered
/// against unambiguous lines. Imported word clocks are then anchored to Apple's line onset and
/// limited to Apple's line, so the highlight follows Apple's timeline rather than the source's.
/// </remarks>
public static class NativeLyricsFeatureMerger
{
    private const double AlignmentToleranceSeconds = .5;
    private const double RoundingToleranceSeconds = .000001;

    // Re-registration of a consistently shifted source: at least this many unambiguous lines
    // must agree, the shift is bounded, and most anchors must sit close to the median shift.
    private const int MinimumShiftAnchors = 3;
    private const double MaximumTimelineShiftSeconds = 3;
    private const double ShiftConsensusSeconds = .3;

    public static LyricsDocument MergeMissingFeatures(LyricsDocument native, LyricsDocument supplement,
        bool words, bool translation)
    {
        if ((!words && !translation) || supplement.Lines.Count == 0
            || string.Equals(supplement.Status, "plain", StringComparison.OrdinalIgnoreCase)
            || !double.IsFinite(native.NativeOffsetSeconds) || !double.IsFinite(supplement.NativeOffsetSeconds))
            return native;

        var candidates = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = 0; index < supplement.Lines.Count; index++)
        {
            var line = supplement.Lines[index];
            if (!IsValidWindow(line)) continue;
            var text = BodyText(line.Text);
            if (text.Length == 0) continue;
            if (!candidates.TryGetValue(text, out var indices)) candidates[text] = indices = [];
            indices.Add(index);
        }

        // Express source times in native coordinates, including any consistent timeline shift.
        var sourceToNative = supplement.NativeOffsetSeconds - native.NativeOffsetSeconds;
        sourceToNative -= EstimateTimelineShift(native, supplement, candidates, sourceToNative);

        LyricsLine[]? merged = null;
        var lastMatchedIndex = -1;
        for (var index = 0; index < native.Lines.Count; index++)
        {
            var line = native.Lines[index];
            if (!IsValidWindow(line) || !candidates.TryGetValue(BodyText(line.Text), out var indices)) continue;
            var bestIndex = -1;
            var bestDistance = double.PositiveInfinity;
            var ambiguous = false;
            foreach (var candidateIndex in indices)
            {
                if (candidateIndex <= lastMatchedIndex) continue;
                var candidate = supplement.Lines[candidateIndex];
                var distance = Math.Abs(candidate.Begin + sourceToNative - line.Begin);
                if (distance > AlignmentToleranceSeconds + RoundingToleranceSeconds) continue;
                if (distance + RoundingToleranceSeconds < bestDistance)
                {
                    bestIndex = candidateIndex;
                    bestDistance = distance;
                    ambiguous = false;
                }
                else if (Math.Abs(distance - bestDistance) <= RoundingToleranceSeconds)
                    ambiguous = true;
            }
            // Do not guess between indistinguishable repeated lines with different source features.
            if (bestIndex < 0 || ambiguous) continue;
            lastMatchedIndex = bestIndex;
            var matched = supplement.Lines[bestIndex];
            var importedWords = words && line.Words is not { Count: > 0 }
                ? BindWords(line, matched) : null;
            var importedTranslation = translation && !LyricsTranslationFilter.IsMeaningful(line.Translation)
                && HasMeaningfulTranslation(matched) ? matched.Translation : null;
            if (importedWords is null && importedTranslation is null) continue;
            merged ??= native.Lines.ToArray();
            merged[index] = line with
            {
                Words = importedWords ?? line.Words,
                Translation = importedTranslation ?? line.Translation,
            };
        }
        return merged is null ? native : native with { Lines = merged };
    }

    internal static bool HasMeaningfulTranslation(LyricsLine line) =>
        LyricsTranslationFilter.IsMeaningful(line.Translation)
        && MetadataMatching.Normalize(line.Translation) != MetadataMatching.Normalize(line.Text);

    private static bool IsValidWindow(LyricsLine line) => double.IsFinite(line.Begin)
        && double.IsFinite(line.End) && line.Begin >= 0 && line.End > line.Begin;

    /// <summary>
    /// Median onset difference over lines whose text occurs exactly once in each document, or zero
    /// when too few such lines agree. Re-registers a source that is consistently early or late.
    /// </summary>
    private static double EstimateTimelineShift(LyricsDocument native, LyricsDocument supplement,
        Dictionary<string, List<int>> candidates, double sourceToNative)
    {
        var nativeTexts = native.Lines.Where(IsValidWindow).GroupBy(line => BodyText(line.Text))
            .Where(group => group.Key.Length > 0 && group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var shifts = new List<double>();
        foreach (var (text, indices) in candidates)
        {
            if (indices.Count != 1 || !nativeTexts.TryGetValue(text, out var line)) continue;
            var shift = supplement.Lines[indices[0]].Begin + sourceToNative - line.Begin;
            if (Math.Abs(shift) <= MaximumTimelineShiftSeconds) shifts.Add(shift);
        }
        if (shifts.Count < MinimumShiftAnchors) return 0;
        shifts.Sort();
        var median = shifts.Count % 2 == 1 ? shifts[shifts.Count / 2]
            : (shifts[shifts.Count / 2 - 1] + shifts[shifts.Count / 2]) / 2;
        var agreeing = shifts.Count(shift => Math.Abs(shift - median) <= ShiftConsensusSeconds);
        return agreeing * 2 > shifts.Count ? median : 0;
    }

    private static IReadOnlyList<LyricWord>? BindWords(LyricsLine native, LyricsLine candidate)
    {
        if (!LyricWordTiming.IsUsable(candidate)) return null;
        // Anchor the source's first sung syllable to Apple's onset for this line. The source keeps
        // its own syllable rhythm; its onset, already within the pairing tolerance, yields to Apple.
        var firstTimed = candidate.Words!.FirstOrDefault(word => BodyRunes(word.Text).Count > 0);
        if (firstTimed is null) return null;
        var offsetDelta = native.Begin - firstTimed.Begin;
        var body = BodyRunes(native.Text);
        var cursor = 0;
        var result = new List<LyricWord>();
        var lastBegin = double.NegativeInfinity;
        foreach (var word in candidate.Words!)
        {
            var wordBody = BodyRunes(word.Text);
            // Whitespace/punctuation gaps in the native text need not be timed words.
            if (wordBody.Count == 0) continue;
            var first = cursor;
            foreach (var rune in wordBody)
            {
                if (cursor >= body.Count || rune.Value != body[cursor].Value) return null;
                cursor++;
            }
            var begin = word.Begin + offsetDelta;
            var end = word.End + offsetDelta;
            // A syllable that would start after Apple's line has ended cannot be shown on it. One
            // that is still sounding when the line ends is cut at that end rather than rejected.
            if (!double.IsFinite(begin) || !double.IsFinite(end)
                || begin < native.Begin - RoundingToleranceSeconds
                || begin >= native.End || end < begin
                || begin < lastBegin - RoundingToleranceSeconds) return null;
            begin = Math.Clamp(begin, native.Begin, native.End);
            end = Math.Clamp(end, native.Begin, native.End);
            lastBegin = begin;
            var start = body[first].Start;
            var finish = body[cursor - 1].End;
            result.Add(new LyricWord(begin, end, native.Text[start..finish]));
        }
        if (cursor != body.Count || result.Count == 0) return null;
        return LyricWordTiming.IsUsable(native with { Words = result }) ? result.ToArray() : null;
    }

    private static string BodyText(string text)
    {
        var result = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
            if (!Rune.IsWhiteSpace(rune) && !Rune.IsPunctuation(rune)) result.Append(Rune.ToUpperInvariant(rune));
        return result.ToString();
    }

    private static List<BodyRune> BodyRunes(string text)
    {
        var result = new List<BodyRune>();
        var position = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var end = position + rune.Utf16SequenceLength;
            if (!Rune.IsWhiteSpace(rune) && !Rune.IsPunctuation(rune))
                result.Add(new BodyRune(Rune.ToUpperInvariant(rune), position, end));
            position = end;
        }
        return result;
    }

    private readonly record struct BodyRune(Rune Value, int Start, int End);
}

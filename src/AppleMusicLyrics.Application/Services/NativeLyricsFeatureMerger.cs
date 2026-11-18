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
    private const double MaximumTranslationPartGapSeconds = 2;

    // Re-registration of a consistently shifted source: at least this many unambiguous lines
    // must agree, the shift is bounded, and most anchors must sit close to the median shift.
    private const int MinimumShiftAnchors = 3;
    private const double MaximumTimelineShiftSeconds = 3;
    private const double ShiftConsensusSeconds = .3;

    // An outright replacement must be the same lyrics: at least this share of Apple's sung lines
    // (and of each such line's words) must be found in the candidate at the same time.
    private const double MinimumSameLyricsCoverage = .6;

    public static LyricsDocument MergeMissingFeatures(LyricsDocument native, LyricsDocument supplement,
        bool words, bool translation)
    {
        if ((!words && !translation) || !CanAlign(native, supplement)) return native;

        var (pairs, sourceToNative) = PairLines(native, supplement);
        LyricsLine[]? merged = null;
        foreach (var (index, sourceIndex) in pairs)
        {
            var line = native.Lines[index];
            var matched = supplement.Lines[sourceIndex];
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
        if (translation && pairs.Count > 1)
            merged = FillSplitTranslations(native.Lines, supplement.Lines, pairs, sourceToNative, merged);
        return merged is null ? native : native with { Lines = merged };
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is the same lyrics as <paramref name="native"/> on
    /// the same timeline, even when the provider splits or words lines differently: most of
    /// Apple's sung lines must find most of their words among the candidate lines sung at the same
    /// time. Guards an outright replacement against a different song or recording.
    /// </summary>
    public static bool MatchesNativeLyrics(LyricsDocument native, LyricsDocument candidate)
    {
        if (!CanAlign(native, candidate)) return false;
        var nativeLines = native.Lines.Where(IsValidWindow).Select(line => (Line: line, Words: WordTokens(line.Text)))
            .Where(item => item.Words.Count > 0).ToList();
        if (nativeLines.Count == 0) return false;
        var candidateLines = candidate.Lines.Where(IsValidWindow).Select(line => (Line: line, Words: WordTokens(line.Text)))
            .Where(item => item.Words.Count > 0).ToList();
        var sourceToNative = candidate.NativeOffsetSeconds - native.NativeOffsetSeconds;

        var anchored = 0;
        foreach (var (line, words) in nativeLines)
        {
            var heard = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (other, otherWords) in candidateLines)
            {
                if (other.Begin + sourceToNative < line.End + SameLyricsTimeToleranceSeconds
                    && other.End + sourceToNative > line.Begin - SameLyricsTimeToleranceSeconds)
                    heard.UnionWith(otherWords);
            }
            if (words.Count(heard.Contains) >= words.Count * MinimumSameLyricsCoverage) anchored++;
        }
        return anchored >= Math.Max(1, (int)Math.Ceiling(nativeLines.Count * MinimumSameLyricsCoverage));
    }

    private const double SameLyricsTimeToleranceSeconds = 1.0;

    // Distinct upper-cased words; each CJK/kana/hangul character counts as its own word.
    private static HashSet<string> WordTokens(string text) => new(OrderedWordTokens(text), StringComparer.Ordinal);

    // Upper-cased words in order; each CJK/kana/hangul character counts as its own word.
    private static List<string> OrderedWordTokens(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                if (Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.OtherLetter)
                {
                    if (current.Length > 0) result.Add(current.ToString());
                    current.Clear();
                    result.Add(Rune.ToUpperInvariant(rune).ToString());
                    continue;
                }
                current.Append(Rune.ToUpperInvariant(rune).ToString());
                continue;
            }
            if (current.Length > 0) result.Add(current.ToString());
            current.Clear();
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    // Re-timing onto Apple's line starts: how far a source line may be from Apple's onset to
    // still count as the same line start, and the largest correction trusted.
    private const double RetimeSearchSeconds = 1.6;
    private const double MaximumRetimeShiftSeconds = 3.0;
    private const double MinimumAnchorSpacingSeconds = .05;

    /// <summary>
    /// Moves a replacement document onto Apple's line timeline. Some providers (QQ Music in
    /// particular) start individual lines a few hundred milliseconds early or late, and not by
    /// a constant amount, so no single offset fixes them. Each Apple line start anchors the
    /// source line that begins with the same word; every source time (lines, syllables,
    /// background vocals) is then mapped through a monotonic piecewise-linear correction between
    /// those anchors. Lines the source splits get an interpolated correction, the source's own
    /// syllable rhythm is kept, and order is preserved. Returns <paramref name="candidate"/>
    /// itself when it already agrees with Apple or too few anchors are found.
    /// </summary>
    public static LyricsDocument AlignToNativeTimeline(LyricsDocument native, LyricsDocument candidate)
    {
        if (!CanAlign(native, candidate)) return candidate;
        var sourceToNative = candidate.NativeOffsetSeconds - native.NativeOffsetSeconds;
        var sourceLines = candidate.Lines
            .Select((line, index) => (Line: line, Index: index, First: OrderedWordTokens(line.Text).FirstOrDefault()))
            .Where(item => IsValidWindow(item.Line) && item.First is not null)
            .ToList();

        var anchors = new List<(double Source, double Target)>();
        var lastSourceIndex = -1;
        foreach (var line in native.Lines)
        {
            if (!IsValidWindow(line) || OrderedWordTokens(line.Text).FirstOrDefault() is not { } first) continue;
            // Apple's onset expressed in the source's own time coordinates.
            var target = line.Begin - sourceToNative;
            var match = sourceLines
                .Where(item => item.Index > lastSourceIndex && item.First == first
                    && Math.Abs(item.Line.Begin - target) <= RetimeSearchSeconds)
                .OrderBy(item => Math.Abs(item.Line.Begin - target))
                .FirstOrDefault();
            if (match.Line is null || Math.Abs(target - match.Line.Begin) > MaximumRetimeShiftSeconds) continue;
            if (anchors.Count > 0)
            {
                var (previousSource, previousTarget) = anchors[^1];
                // Both timelines must advance; a correction may stretch or squeeze a gap but not
                // fold it, or the mapping would reorder lines.
                if (match.Line.Begin - previousSource < MinimumAnchorSpacingSeconds
                    || target - previousTarget < MinimumAnchorSpacingSeconds) continue;
                var slope = (target - previousTarget) / (match.Line.Begin - previousSource);
                if (slope is < .5 or > 2) continue;
            }
            anchors.Add((match.Line.Begin, target));
            lastSourceIndex = match.Index;
        }

        if (anchors.Count < 2 || anchors.All(anchor => Math.Abs(anchor.Target - anchor.Source) < .001)) return candidate;

        double Map(double time)
        {
            if (!double.IsFinite(time)) return time;
            if (time <= anchors[0].Source) return Math.Max(0, time + anchors[0].Target - anchors[0].Source);
            if (time >= anchors[^1].Source) return time + anchors[^1].Target - anchors[^1].Source;
            var upper = 1;
            while (anchors[upper].Source < time) upper++;
            var (s0, t0) = anchors[upper - 1];
            var (s1, t1) = anchors[upper];
            return t0 + (time - s0) * (t1 - t0) / (s1 - s0);
        }

        LyricsLine MapLine(LyricsLine line) => line with
        {
            Begin = Map(line.Begin),
            End = Map(line.End),
            Words = line.Words?.Select(word => word with { Begin = Map(word.Begin), End = Map(word.End) }).ToArray(),
            BackgroundVocals = line.BackgroundVocals?.Select(MapLine).ToArray(),
        };

        return candidate with { Lines = candidate.Lines.Select(MapLine).ToArray() };
    }

    private static bool CanAlign(LyricsDocument native, LyricsDocument supplement) => supplement.Lines.Count > 0
        && !string.Equals(supplement.Status, "plain", StringComparison.OrdinalIgnoreCase)
        && double.IsFinite(native.NativeOffsetSeconds) && double.IsFinite(supplement.NativeOffsetSeconds);

    /// <summary>Pairs native lines with supplement lines in order; returns the pairs and the source-to-native shift.</summary>
    private static (List<(int Native, int Source)> Pairs, double SourceToNative) PairLines(
        LyricsDocument native, LyricsDocument supplement)
    {
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

        var pairs = new List<(int Native, int Source)>();
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
            pairs.Add((index, bestIndex));
        }
        return (pairs, sourceToNative);
    }

    private static LyricsLine[]? FillSplitTranslations(IReadOnlyList<LyricsLine> native, IReadOnlyList<LyricsLine> source,
        List<(int Native, int Source)> pairs, double sourceToNative, LyricsLine[]? merged)
    {
        // Only join short spans bounded by two independently verified line matches. Providers
        // can divide the same phrase at different words or translate it as one sentence.
        for (var index = 1; index < pairs.Count; index++)
        {
            var (nativeStart, sourceStart) = pairs[index - 1];
            var (nativeEnd, sourceEnd) = pairs[index];
            nativeStart++;
            sourceStart++;
            var nativeCount = nativeEnd - nativeStart;
            var sourceCount = sourceEnd - sourceStart;
            if (nativeCount is < 1 or > 2 || sourceCount is < 1 or > 2 || (nativeCount == 1 && sourceCount == 1)) continue;

            var nativeFirst = native[nativeStart];
            var nativeLast = native[nativeEnd - 1];
            var sourceFirst = source[sourceStart];
            var sourceLast = source[sourceEnd - 1];
            if (!IsValidWindow(nativeFirst) || !IsValidWindow(sourceFirst)
                || (nativeCount == 2 && !CanJoinTranslationParts(nativeFirst, nativeLast))
                || (sourceCount == 2 && !CanJoinTranslationParts(sourceFirst, sourceLast))
                || Math.Abs(sourceFirst.Begin + sourceToNative - nativeFirst.Begin) > AlignmentToleranceSeconds
                || sourceLast.Begin + sourceToNative >= nativeLast.End + AlignmentToleranceSeconds
                || sourceLast.End + sourceToNative < nativeLast.Begin - AlignmentToleranceSeconds
                || LyricsTranslationFilter.IsMeaningful(nativeFirst.Translation)
                || LyricsTranslationFilter.IsMeaningful(nativeLast.Translation)
                || !HasMeaningfulTranslation(sourceFirst) || !HasMeaningfulTranslation(sourceLast))
                continue;

            var nativeText = BodyText(nativeFirst.Text) + (nativeCount == 2 ? BodyText(nativeLast.Text) : string.Empty);
            var sourceText = BodyText(sourceFirst.Text) + (sourceCount == 2 ? BodyText(sourceLast.Text) : string.Empty);
            if (nativeText.Length == 0 || !string.Equals(nativeText, sourceText, StringComparison.Ordinal)) continue;

            var firstTranslation = LyricsTranslationFilter.Clean(sourceFirst.Translation);
            var lastTranslation = LyricsTranslationFilter.Clean(sourceLast.Translation);
            var phrase = firstTranslation == lastTranslation ? firstTranslation : firstTranslation + " " + lastTranslation;
            // Keep the full translation visible through the phrase, including when seeking into
            // its second line. The provider did not supply a way to split the translated text.
            merged ??= native.ToArray();
            for (var line = nativeStart; line < nativeEnd; line++)
                merged[line] = native[line] with { Translation = phrase };
        }
        return merged;
    }

    private static bool CanJoinTranslationParts(LyricsLine first, LyricsLine second) => IsValidWindow(second)
        && first.VocalistId == second.VocalistId && second.Begin > first.Begin
        && second.Begin >= first.End - RoundingToleranceSeconds && second.Begin - first.End <= MaximumTranslationPartGapSeconds;

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

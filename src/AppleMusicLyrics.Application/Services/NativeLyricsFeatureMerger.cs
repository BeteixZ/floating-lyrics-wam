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
public static class NativeLyricsFeatureMerger
{
    private const double AlignmentToleranceSeconds = .5;
    private const double RoundingToleranceSeconds = .000001;

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
                var beginDelta = Math.Abs(line.Begin + native.NativeOffsetSeconds
                    - candidate.Begin - supplement.NativeOffsetSeconds);
                var endDelta = Math.Abs(line.End + native.NativeOffsetSeconds
                    - candidate.End - supplement.NativeOffsetSeconds);
                if (beginDelta > AlignmentToleranceSeconds || endDelta > AlignmentToleranceSeconds) continue;
                var distance = beginDelta + endDelta;
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
                ? BindWords(line, matched, supplement.NativeOffsetSeconds - native.NativeOffsetSeconds) : null;
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

    private static IReadOnlyList<LyricWord>? BindWords(LyricsLine native, LyricsLine candidate, double offsetDelta)
    {
        if (!LyricWordTiming.IsUsable(candidate)) return null;
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
            if (!double.IsFinite(begin) || !double.IsFinite(end)
                || begin < native.Begin - RoundingToleranceSeconds
                || end > native.End + RoundingToleranceSeconds || end < begin
                || begin < lastBegin - RoundingToleranceSeconds) return null;
            // Only machine-rounding noise is clamped; real timing disagreement is rejected.
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

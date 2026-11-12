using AppleMusicLyrics.Core.Matching;
using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Core.Parsing;

public static class LyricsTranslationMerger
{
    public static IReadOnlyList<LyricsLine> MergeLines(IReadOnlyList<LyricsLine> original, IReadOnlyList<LyricsLine> translated)
    {
        if (original.Count == 0 || translated.Count == 0) return original;
        var normalizedOriginal = original.Select(line => MetadataMatching.Normalize(line.Text)).ToArray();
        var skipTranslated = new bool[translated.Count];
        var cleanedTranslated = new string?[translated.Count];
        for (var candidate = 0; candidate < translated.Count; candidate++)
        {
            var normalized = MetadataMatching.Normalize(translated[candidate].Text);
            cleanedTranslated[candidate] = LyricsTranslationFilter.Clean(translated[candidate].Text);
            // Placeholder ("//") and credit rows still occupy their timestamp, so they consume
            // nothing here and cannot claim a neighbouring lyric's slot either.
            skipTranslated[candidate] = cleanedTranslated[candidate] is null
                || original.Select((line, index) => (line, index)).Any(item =>
                    normalizedOriginal[item.index] == normalized
                    && Math.Abs(item.line.Begin - translated[candidate].Begin) <= 0.5);
        }
        var pairs = new List<(int Original, int Translated, double Delta)>();
        for (var index = 0; index < original.Count; index++)
        {
            var line = original[index];
            // A placeholder or credit left by an earlier source may still be filled by a later one.
            if (LyricsTranslationFilter.IsMeaningful(line.Translation)) continue;
            for (var candidate = 0; candidate < translated.Count; candidate++)
            {
                if (skipTranslated[candidate]) continue;
                var delta = Math.Abs(line.Begin - translated[candidate].Begin);
                if (double.IsFinite(delta) && delta <= 0.5)
                {
                    pairs.Add((index, candidate, delta));
                }
            }
        }
        var result = original.ToArray();
        var usedOriginal = new HashSet<int>();
        var usedTranslated = new HashSet<int>();
        // Exact timestamps must not lose their translation to an earlier nearby line.
        foreach (var pair in pairs.OrderBy(pair => pair.Delta).ThenBy(pair => pair.Original).ThenBy(pair => pair.Translated))
        {
            if (usedOriginal.Contains(pair.Original) || usedTranslated.Contains(pair.Translated)) continue;
            usedOriginal.Add(pair.Original);
            usedTranslated.Add(pair.Translated);
            result[pair.Original] = original[pair.Original] with { Translation = cleanedTranslated[pair.Translated] };
        }
        return result;
    }
}

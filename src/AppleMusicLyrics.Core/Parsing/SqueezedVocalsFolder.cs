using System.Runtime.CompilerServices;
using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Core.Parsing;

/// <summary>
/// Some sources write simultaneous vocals one after another. In QQ Music's "Breakdown", a
/// backing "Break-breakdown" sung over Mariah Carey's line becomes its own 0.3 s line squeezed
/// between hers, so the display flickers between lines faster than anyone can read them.
/// Such slivers are folded into the main line they interrupt as background vocals, which the
/// overlay shows as smaller text under that line, the way Apple Music shows backing vocals.
/// The fragments' own timestamps are not when they are sung, so they are shown without
/// word-by-word highlighting.
/// </summary>
public static class SqueezedVocalsFolder
{
    // Fold each immutable document once; the display reads the result every frame.
    private static readonly ConditionalWeakTable<LyricsDocument, LyricsDocument> Folded = new();

    // A sliver is too short to read and denser than anyone sings: "Steady breaking me on down"
    // in 0.65 s is 34 letters a second, while a held "Yeah" (11 a second) stays a line.
    internal const double MaxSliverSeconds = 0.8;
    internal const double MinSliverLettersPerSecond = 15;
    // The line a sliver folds into must be long enough to carry it, and directly adjacent.
    internal const double MinMainLineSeconds = 1.5;
    internal const double MaxGapSeconds = 0.2;

    public static LyricsDocument Fold(LyricsDocument document) => Folded.GetValue(document, FoldCore);

    private static LyricsDocument FoldCore(LyricsDocument document)
    {
        var lines = document.Lines;
        // Apple's own documents already mark backing vocals and tag who sings each line; never
        // restructure those. A short tagged reply ("Shake your ass" in "It's Like That") is
        // another singer taking over, not a squeezed fragment.
        if (lines.Count < 3 || lines.Any(line => line.BackgroundVocals is { Count: > 0 } || !string.IsNullOrEmpty(line.VocalistId)))
            return document;

        var result = new List<LyricsLine>(lines.Count);
        var pending = new List<LyricsLine>();
        var changed = false;
        foreach (var line in lines)
        {
            if (IsSliver(line))
            {
                // A sliver right after a main line belongs to it, as do further slivers chained
                // to it (Attach extends the line's end); otherwise wait for the next line.
                if (pending.Count == 0 && result.Count > 0 && IsMainLine(result[^1])
                    && line.Begin - result[^1].End <= MaxGapSeconds)
                {
                    result[^1] = Attach(result[^1], line);
                    changed = true;
                }
                else
                {
                    pending.Add(line);
                }
                continue;
            }

            if (pending.Count > 0 && IsMainLine(line) && line.Begin - pending[^1].End <= MaxGapSeconds
                && IsChain(pending))
            {
                // Slivers leading into a line: show them with it, starting when the first is sung.
                result.Add(line with { Begin = Math.Min(line.Begin, pending[0].Begin), BackgroundVocals = [.. pending.Select(Backing)] });
                pending.Clear();
                changed = true;
                continue;
            }

            result.AddRange(pending);
            pending.Clear();
            result.Add(line);
        }
        result.AddRange(pending);

        return changed ? document with { Lines = result.Select(MergeBackground).ToArray() } : document;
    }

    // One small row per line, wrapping to at most two lines, rather than a row per fragment.
    private static LyricsLine MergeBackground(LyricsLine line) =>
        line.BackgroundVocals is { Count: > 1 } parts
            ? line with
            {
                BackgroundVocals = [parts[0] with
                {
                    End = parts.Max(part => part.End),
                    Text = string.Join(Separator, parts.Select(part => part.Text.Trim())),
                }],
            }
            : line;

    // Sources record no time for a squeezed fragment, only the slot they moved it to, which
    // comes after the moment it is sung. Show its text for the whole lead line instead of
    // highlighting it at that slot; its translation, if any, is not shown either.
    private static LyricsLine Backing(LyricsLine sliver) =>
        sliver with { Words = null, Translation = null, Romanization = null };

    private const string Separator = " · ";

    private static LyricsLine Attach(LyricsLine main, LyricsLine sliver) => main with
    {
        End = Math.Max(main.End, sliver.End),
        BackgroundVocals = [.. main.BackgroundVocals ?? [], Backing(sliver)],
    };

    private static bool IsChain(List<LyricsLine> slivers)
    {
        for (var index = 1; index < slivers.Count; index++)
            if (slivers[index].Begin - slivers[index - 1].End > MaxGapSeconds) return false;
        return true;
    }

    private static bool IsMainLine(LyricsLine line) =>
        double.IsFinite(line.Begin) && double.IsFinite(line.End) && line.End - line.Begin >= MinMainLineSeconds;

    internal static bool IsSliver(LyricsLine line)
    {
        var duration = line.End - line.Begin;
        if (!double.IsFinite(duration) || duration <= 0 || duration >= MaxSliverSeconds) return false;
        var letters = line.Text.Count(char.IsLetterOrDigit);
        return letters >= 3 && letters / duration >= MinSliverLettersPerSecond;
    }
}

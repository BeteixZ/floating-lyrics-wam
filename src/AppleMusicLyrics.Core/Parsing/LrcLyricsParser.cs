using System.Globalization;
using System.Text.RegularExpressions;
using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Core.Parsing;

/// <summary>
/// Parses the LRC format used by every lyrics source outside Apple's TTML.
///
/// LRC only marks where a line <em>starts</em>, so end times are derived from the following entry.
/// A timestamped line with no text is the format's way of saying "stop showing the previous line";
/// those entries close the preceding line and are then dropped rather than becoming blank lyrics.
/// </summary>
public sealed class LrcLyricsParser
{
    // [mm:ss], [mm:ss.xx] and [mm:ss.xxx] are all in the wild, as is a colon before the fraction.
    private static readonly Regex TimestampPattern = new(
        @"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TagPattern = new(
        @"^\[([a-zA-Z]+):(.*)\]$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // How long the closing line stays up when the source gave us no track duration to end it at.
    private const double DefaultTailSeconds = 8.0;

    public LyricsDocument Parse(
        string lrcText,
        string sourceFile,
        string? lyricsId = null,
        double? durationSeconds = null)
    {
        var lines = ParseLines(lrcText, durationSeconds);

        return new LyricsDocument(
            LyricsId: lyricsId,
            Status: "success",
            SourceFile: sourceFile,
            UpdatedAt: DateTimeOffset.UtcNow,
            Lines: lines,
            DurationSeconds: durationSeconds);
    }

    public IReadOnlyList<LyricsLine> ParseLines(string lrcText, double? durationSeconds = null)
    {
        if (string.IsNullOrWhiteSpace(lrcText))
        {
            return Array.Empty<LyricsLine>();
        }

        var offsetSeconds = 0.0;
        var entries = new List<(double Begin, string Text)>();

        foreach (var rawLine in lrcText.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0)
            {
                continue;
            }

            var timestamps = ReadLeadingTimestamps(line, out var textStart);
            if (timestamps.Count == 0)
            {
                var tag = TagPattern.Match(line);
                if (tag.Success && string.Equals(tag.Groups[1].Value, "offset", StringComparison.OrdinalIgnoreCase))
                {
                    offsetSeconds = ParseOffsetSeconds(tag.Groups[2].Value);
                }

                continue;
            }

            var text = line[textStart..].Trim();
            foreach (var timestamp in timestamps)
            {
                entries.Add((timestamp, text));
            }
        }

        if (entries.Count == 0)
        {
            return Array.Empty<LyricsLine>();
        }

        // "+" means the lyrics should appear earlier, so it comes off the timestamps. Baking it in
        // here keeps an LRC document self-contained instead of carrying a correction alongside it.
        if (offsetSeconds != 0.0)
        {
            for (var index = 0; index < entries.Count; index++)
            {
                entries[index] = (entries[index].Begin - offsetSeconds, entries[index].Text);
            }
        }

        entries.Sort((left, right) => left.Begin.CompareTo(right.Begin));

        return BuildLines(entries, durationSeconds);
    }

    private static IReadOnlyList<LyricsLine> BuildLines(
        List<(double Begin, string Text)> entries,
        double? durationSeconds)
    {
        var lines = new List<LyricsLine>(entries.Count);

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.Text.Length == 0)
            {
                // A bare timestamp only closes whatever came before it.
                continue;
            }

            var end = index + 1 < entries.Count
                ? entries[index + 1].Begin
                : ResolveFinalEnd(entry.Begin, durationSeconds);

            if (end <= entry.Begin)
            {
                end = entry.Begin + DefaultTailSeconds;
            }

            lines.Add(new LyricsLine(Begin: Math.Max(0, entry.Begin), End: end, Text: entry.Text));
        }

        return lines;
    }

    private static double ResolveFinalEnd(double begin, double? durationSeconds)
    {
        return durationSeconds is > 0 && durationSeconds > begin
            ? durationSeconds.Value
            : begin + DefaultTailSeconds;
    }

    private static List<double> ReadLeadingTimestamps(string line, out int textStart)
    {
        var timestamps = new List<double>();
        var cursor = 0;

        while (cursor < line.Length)
        {
            var match = TimestampPattern.Match(line, cursor);
            if (!match.Success || match.Index != cursor)
            {
                break;
            }

            timestamps.Add(ToSeconds(match));
            cursor = match.Index + match.Length;
        }

        textStart = cursor;
        return timestamps;
    }

    private static double ToSeconds(Match match)
    {
        var minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);

        var fraction = 0.0;
        if (match.Groups[3].Success)
        {
            var digits = match.Groups[3].Value;
            var value = int.Parse(digits, CultureInfo.InvariantCulture);

            // Two digits are hundredths, three are thousandths, one is tenths.
            fraction = digits.Length switch
            {
                1 => value / 10.0,
                2 => value / 100.0,
                _ => value / 1000.0,
            };
        }

        return (minutes * 60) + seconds + fraction;
    }

    private static double ParseOffsetSeconds(string rawValue)
    {
        var trimmed = rawValue.Trim().TrimStart('+');
        return int.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var milliseconds)
            ? milliseconds / 1000.0
            : 0.0;
    }
}

// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from MxIris-LyricsX-Project/LyricsKit at
// 6f071990d0c9c6f48d29284db805990bffb912ed,
// Sources/LyricsService/Parser/NetEaseKLyricParser.swift and
// Sources/LyricsService/Utilities/RegexPattern.swift.

using System.Globalization;
using System.Text.RegularExpressions;
using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Infrastructure.Windows.External;

internal static class NetEaseTimedLyricsParser
{
    private static readonly Regex LinePattern = new(
        @"^\[(\d+),(\d+)\](.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // YRC contains absolute word starts and durations; older klyric uses (0,duration).
    private static readonly Regex WordPattern = new(
        @"\((\d+),(\d+)(?:,(\d+))?\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static IReadOnlyList<LyricsLine> Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var lines = new List<LyricsLine>();
        foreach (var rawLine in content.Split('\n'))
        {
            var match = LinePattern.Match(rawLine.Trim());
            if (!match.Success
                || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var beginMs)
                || !long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var lengthMs)
                || lengthMs <= 0)
            {
                continue;
            }

            var contentText = match.Groups[3].Value;
            var text = WordPattern.Replace(contentText, "").Trim();
            if (text.Length > 0)
            {
                var begin = beginMs / 1000.0;
                lines.Add(new LyricsLine(begin, begin + lengthMs / 1000.0, text,
                    Words: ParseWords(contentText, begin, text)));
            }
        }

        return lines.OrderBy(line => line.Begin).ToArray();
    }

    private static IReadOnlyList<LyricWord>? ParseWords(string content, double lineBegin, string lineText)
    {
        var markers = WordPattern.Matches(content);
        if (markers.Count == 0)
        {
            return null;
        }

        var yrc = markers[0].Groups[3].Success;
        var words = new List<LyricWord>();
        var relativePosition = lineBegin;
        for (var index = 0; index < markers.Count; index++)
        {
            var marker = markers[index];
            if (marker.Groups[3].Success != yrc
                || (yrc && marker.Groups[3].Value != "0")
                || !long.TryParse(marker.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var startMs)
                || !long.TryParse(marker.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var durationMs)
                || (!yrc && startMs != 0))
            {
                return null;
            }

            var textStart = marker.Index + marker.Length;
            var textEnd = index + 1 < markers.Count ? markers[index + 1].Index : content.Length;
            var fragment = content[textStart..textEnd];
            var begin = yrc ? startMs / 1000.0 : relativePosition;
            var end = begin + durationMs / 1000.0;
            relativePosition = end;
            if (durationMs <= 0 || fragment.Length == 0)
            {
                continue;
            }

            // Upstream klyric places an extra (0,1) marker before some spaces. It consumes
            // one millisecond and belongs to the previous word, rather than a visible syllable.
            if (!yrc && durationMs == 1 && string.IsNullOrWhiteSpace(fragment) && words.Count > 0)
            {
                var previous = words[^1];
                words[^1] = previous with { End = end, Text = previous.Text + fragment };
            }
            else
            {
                words.Add(new LyricWord(begin, end, fragment));
            }
        }

        if (words.Count == 0)
        {
            return null;
        }
        words[0] = words[0] with { Text = words[0].Text.TrimStart() };
        words[^1] = words[^1] with { Text = words[^1].Text.TrimEnd() };
        words.RemoveAll(word => word.Text.Length == 0);
        // Preserve unmarked text without inventing timing for it. A partly corrupt word stream
        // remains a valid line, but cannot safely drive a complete word-by-word highlight.
        return words.Count > 0 && string.Concat(words.Select(word => word.Text)) == lineText ? words : null;
    }
}

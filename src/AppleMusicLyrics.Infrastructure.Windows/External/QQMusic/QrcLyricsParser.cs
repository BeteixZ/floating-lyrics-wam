// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Adapted from MxIris-LyricsX-Project/LyricsKit, revision 6f071990d0c9c6f48d29284db805990bffb912ed.
// Sources: Sources/LyricsService/Parser/QQMusicQrcParser.swift
//          Sources/LyricsService/Provider/Services/QQMusic.swift
//          Sources/LyricsService/Parser/QQMusicQrcDecrypter.swift (XMLUtils cleanup)
//          Sources/LyricsService/Utilities/RegexPattern.swift (QRC word tag format)

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Parsing;

namespace AppleMusicLyrics.Infrastructure.Windows.External;

internal static class QrcLyricsParser
{
    private static readonly Regex QrcLine = new(@"\[(\d+),(\d+)\]([^\r\n]*)", RegexOptions.CultureInvariant);
    private static readonly Regex WordTime = new(@"\((\d+),(\d+)\)", RegexOptions.CultureInvariant);
    private static readonly Regex Hex = new(@"\A[0-9a-fA-F]+\z", RegexOptions.CultureInvariant);
    private static readonly Regex ExtendedTime = new(@"\[(\d+):(\d{2}):(\d{2}(?:\.\d+)?)\]", RegexOptions.CultureInvariant);
    // Match the closing attribute quote by its following XML syntax. QQ sometimes leaves quotes
    // and ampersands in lyric text unescaped, and XML DOM parsing also normalizes literal newlines.
    private static readonly Regex LyricAttribute = new(
        "\\bLyricContent\\s*=\\s*(?<quote>[\"'])(?<value>[\\s\\S]*?)\\k<quote>(?=\\s*(?:/?\\s*>|[\\w:.-]+\\s*=))",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex SelfClosingElement = new(@"<[^<>]*?/\s*>",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex RecordingTag = new(@"^[ \t]*\[(ti|ar):([^\]\r\n]*)\]",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static LyricsDocument? ParseResponse(string response, string id, double? duration, PlayerState? player = null)
    {
        // QQ wraps its XML payload in HTML comment markers.
        var outer = ReadXml(response.Replace("<!--", string.Empty, StringComparison.Ordinal)
            .Replace("-->", string.Empty, StringComparison.Ordinal));
        var text = outer.Descendants().FirstOrDefault(node => node.Name.LocalName == "content")?.Value;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var originalContent = DecodeContent(text);
        if (player is not null && !MatchesRecordingMetadata(originalContent, player))
        {
            return null;
        }
        var original = ParseContent(originalContent, id, duration);
        if (original is null)
        {
            return null;
        }

        var translation = outer.Descendants().FirstOrDefault(node => node.Name.LocalName == "contentts")?.Value;
        if (!string.IsNullOrWhiteSpace(translation))
        {
            try
            {
                var translated = ParseContent(DecodeContent(translation), id, duration);
                if (translated is not null)
                {
                    original = original with
                    {
                        Lines = LyricsTranslationMerger.MergeLines(original.Lines, translated.Lines),
                    };
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or XmlException
                or DecoderFallbackException or RegexMatchTimeoutException or OverflowException)
            {
                // A broken optional translation must not suppress the original lyrics.
            }
        }
        return original;
    }

    private static bool MatchesRecordingMetadata(string content, PlayerState player)
    {
        string? title = null, artist = null;
        foreach (Match tag in RecordingTag.Matches(content))
        {
            var value = tag.Groups[2].Value.Trim();
            if (value.Length == 0)
            {
                continue;
            }
            if (tag.Groups[1].Value.Equals("ti", StringComparison.OrdinalIgnoreCase))
            {
                title ??= value;
            }
            else
            {
                artist ??= value;
            }
        }
        // Smartbox may omit a remix/live suffix that is retained in the actual QRC metadata.
        // Missing tags add no evidence; lyric-end times are not recording-duration metadata.
        return (title is null && artist is null) || PlatformLyricsSupport.ScoreCandidate(player,
            title ?? player.Title, artist ?? player.Artist, null, null) != int.MinValue;
    }

    private static string DecodeContent(string text)
    {
        text = text.Trim();
        var compact = Regex.Replace(text, @"\s+", string.Empty);
        if (compact.Length % 2 == 0 && Hex.IsMatch(compact))
        {
            text = QrcLyricsDecoder.Decode(compact);
        }
        if (text.TrimStart().StartsWith('<'))
        {
            RejectDtd(text);
            var attribute = LyricAttribute.Match(text);
            if (attribute.Success)
            {
                text = WebUtility.HtmlDecode(attribute.Groups["value"].Value);
            }
            else
            {
                var nested = ReadXml(text);
                text = nested.DescendantsAndSelf().Attributes()
                    .FirstOrDefault(attribute => attribute.Name.LocalName == "LyricContent")?.Value ?? string.Empty;
            }
        }
        return text;
    }

    private static LyricsDocument? ParseContent(string text, string id, double? duration)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        // Some payloads have flattened or immediately adjacent QRC line tags.
        text = Regex.Replace(text, @"(?<!\A)(?:[ \t]+)?(?=\[\d+,\d+\])", "\n");
        var lines = new List<LyricsLine>();
        foreach (Match match in QrcLine.Matches(text))
        {
            if (!double.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var begin)
                || !double.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                || !double.IsFinite(begin) || !double.IsFinite(length) || !double.IsFinite(begin + length) || length <= 0)
            {
                continue;
            }
            var body = match.Groups[3].Value;
            var lineText = WordTime.Replace(body, string.Empty);
            if (!string.IsNullOrWhiteSpace(lineText))
            {
                lines.Add(new LyricsLine(begin / 1000, (begin + length) / 1000, lineText,
                    Words: ParseWords(body)));
            }
        }
        var source = $"qqmusic:{id}";
        if (lines.Count == 0)
        {
            text = ExtendedTime.Replace(text, match =>
                "[" + (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 60
                    + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture)
                    + ":" + match.Groups[3].Value + "]");
            var lrc = new LrcLyricsParser().Parse(text, source, $"QQMUSIC_{id}", duration);
            return lrc.Lines.Count > 0 ? lrc : null;
        }
        return new LyricsDocument($"QQMUSIC_{id}", "success", source, DateTimeOffset.UtcNow,
            lines.OrderBy(line => line.Begin).ToArray(), duration);
    }

    private static IReadOnlyList<LyricWord>? ParseWords(string body)
    {
        var words = new List<LyricWord>();
        var cursor = 0;
        foreach (Match match in WordTime.Matches(body))
        {
            var fragment = body[cursor..match.Index];
            cursor = match.Index + match.Length;
            if (fragment.Length == 0
                || !double.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var begin)
                || !double.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                || !double.IsFinite(begin) || !double.IsFinite(length) || !double.IsFinite(begin + length))
            {
                continue;
            }
            // Pinned QRC parser subtracts line start to make relative inline tags. Our Core model
            // stores absolute times, so the raw QRC start is already the correct track position.
            words.Add(new LyricWord(begin / 1000, (begin + length) / 1000, fragment));
        }
        return words.Count > 0 ? words : null;
    }

    private static XElement ReadXml(string content)
    {
        RejectDtd(content);
        // QQ emits malformed metadata such as <miniversion="1" />. Upstream XMLUtils
        // removes self-closing fragments with one '=' and no whitespace before it.
        // Keep proper elements such as <metadata value="1" /> and screen DTDs first.
        content = SelfClosingElement.Replace(content, match =>
        {
            var fragment = match.Value;
            var equals = fragment.IndexOf('=');
            return equals >= 0 && equals == fragment.LastIndexOf('=')
                && !fragment[..equals].Trim().Any(char.IsWhiteSpace) ? string.Empty : fragment;
        });
        using var input = new StringReader(content);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 4 * 1024 * 1024,
        });
        return XElement.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static void RejectDtd(string content)
    {
        if (content.Length > 4 * 1024 * 1024)
        {
            throw new XmlException("QQ XML payload exceeds the size limit.");
        }
        if (content.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
            || content.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase))
        {
            throw new XmlException("QQ lyric XML must not contain a DTD or entity declaration.");
        }
    }
}

using System.Text.Json;
using System.Xml.Linq;
using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Core.Parsing;

public sealed class TtmlLyricsParser
{
    public LyricsDocument ParseLyricsJson(string jsonText, string sourceFile)
    {
        using var document = JsonDocument.Parse(jsonText);
        var root = document.RootElement;

        string? lyricsId = TryGetString(root, "lyricsId");
        string? status = TryGetString(root, "status");
        var ttml = TryGetString(root, "ttml")
            ?? throw new InvalidOperationException("Lyrics JSON is missing the 'ttml' field.");

        var parsed = ParseTtmlDocument(ttml);
        return new LyricsDocument(
            LyricsId: lyricsId,
            Status: status,
            SourceFile: sourceFile,
            UpdatedAt: DateTimeOffset.UtcNow,
            Lines: parsed.Lines,
            DurationSeconds: parsed.DurationSeconds,
            LeadingSilenceSeconds: parsed.LeadingSilenceSeconds,
            NativeOffsetSeconds: parsed.NativeOffsetSeconds);
    }

    public IReadOnlyList<LyricsLine> ParseTtml(string ttmlText)
    {
        return ParseTtmlDocument(ttmlText).Lines;
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string CollapseWhitespace(string text)
    {
        return string.Join(
            " ",
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static ParsedTtmlDocument ParseTtmlDocument(string ttmlText)
    {
        var xml = XDocument.Parse(ttmlText, LoadOptions.PreserveWhitespace);
        var paragraphs = xml.Descendants().Where(element => element.Name.LocalName == "p");
        var lines = new List<LyricsLine>();

        foreach (var paragraph in paragraphs)
        {
            var begin = paragraph.Attribute("begin")?.Value;
            var end = paragraph.Attribute("end")?.Value;
            if (string.IsNullOrWhiteSpace(begin) || string.IsNullOrWhiteSpace(end))
            {
                continue;
            }

            var text = CollapseWhitespace(paragraph.Value);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var songPart = ResolveSongPart(paragraph);
            lines.Add(new LyricsLine(
                Begin: TimeParser.ParseSeconds(begin),
                End: TimeParser.ParseSeconds(end),
                Text: text,
                SongPart: songPart));
        }

        var body = xml.Descendants().FirstOrDefault(element => element.Name.LocalName == "body");
        var durationSeconds = TryParseTimeAttribute(body, "dur");
        var iTunesMetadata = xml.Descendants().FirstOrDefault(element => element.Name.LocalName == "iTunesMetadata");
        var leadingSilenceSeconds = TryParseTimeAttribute(iTunesMetadata, "leadingSilence") ?? 0.0;
        var audioElement = iTunesMetadata?.Descendants().FirstOrDefault(element => element.Name.LocalName == "audio");
        var nativeOffsetSeconds = TryParseTimeAttribute(audioElement, "lyricOffset") ?? 0.0;

        return new ParsedTtmlDocument(lines, durationSeconds, leadingSilenceSeconds, nativeOffsetSeconds);
    }

    private static double? TryParseTimeAttribute(XElement? element, string attributeName)
    {
        var rawValue = element?.Attribute(attributeName)?.Value;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        return TimeParser.ParseSeconds(rawValue);
    }

    private static string? ResolveSongPart(XElement paragraph)
    {
        static string? FindSongPart(XElement element)
        {
            return element.Attributes()
                .FirstOrDefault(attribute =>
                    string.Equals(attribute.Name.LocalName, "songPart", StringComparison.OrdinalIgnoreCase))
                ?.Value;
        }

        return FindSongPart(paragraph)
            ?? paragraph.Ancestors().Select(FindSongPart).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private sealed record ParsedTtmlDocument(
        IReadOnlyList<LyricsLine> Lines,
        double? DurationSeconds,
        double LeadingSilenceSeconds,
        double NativeOffsetSeconds);
}

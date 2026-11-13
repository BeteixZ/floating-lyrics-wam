using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Sync;

namespace AppleMusicLyrics.Core.Parsing;

public sealed class TtmlLyricsParser
{
    public LyricsDocument ParseLyricsJson(string jsonText, string sourceFile)
    {
        using var document = JsonDocument.Parse(jsonText);
        var root = document.RootElement;

        var (resource, attributes) = ResolvePayload(root);
        var ttml = NonEmptyString(attributes, "ttmlLocalizations") ?? NonEmptyString(attributes, "ttml")
            ?? throw new InvalidOperationException("Lyrics JSON is missing a supported TTML payload.");
        var playParams = attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("playParams", out var parameters)
            ? parameters : default;
        string? lyricsId = NonEmptyString(root, "lyricsId") ?? NonEmptyString(resource, "lyricsId")
            ?? NonEmptyString(playParams, "id")
            ?? CatalogLyricsId(NonEmptyString(playParams, "catalogId"))
            ?? (resource.ValueKind != JsonValueKind.Undefined ? CatalogLyricsId(NonEmptyString(resource, "id")) : null);
        string? status = NonEmptyString(root, "status") ?? NonEmptyString(resource, "status")
            ?? (resource.ValueKind != JsonValueKind.Undefined ? "success" : null);

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

    private static (JsonElement Resource, JsonElement Attributes) ResolvePayload(JsonElement root)
    {
        if (NonEmptyString(root, "ttmlLocalizations") is not null || NonEmptyString(root, "ttml") is not null)
            return (default, root);
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
        {
            JsonElement[] resources = data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToArray() : [data];
            foreach (var resource in resources)
            {
                if (resource.ValueKind == JsonValueKind.Object && resource.TryGetProperty("attributes", out var attributes)
                    && (NonEmptyString(attributes, "ttmlLocalizations") is not null || NonEmptyString(attributes, "ttml") is not null))
                    return (resource, attributes);
            }
        }
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("attributes", out var directAttributes))
            return (root, directAttributes);
        return (default, root);
    }

    private static string? CatalogLyricsId(string? value) => value is not null && value.All(char.IsAsciiDigit)
        ? "AP_" + value : value;

    private static string? NonEmptyString(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
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
        var body = xml.Descendants().FirstOrDefault(element => element.Name.LocalName == "body");
        var paragraphs = body?.Descendants().Where(element => element.Name.LocalName == "p") ?? [];
        var translations = ReadTranslations(xml);
        var (romanizationsByKey, romanizationsByBegin) = ReadRomanizations(paragraphs);
        var lines = new List<LyricsLine>();

        foreach (var paragraph in paragraphs)
        {
            var begin = TryParseTimeAttribute(paragraph, "begin");
            var end = TryParseTimeAttribute(paragraph, "end");
            if (!begin.HasValue || !end.HasValue || begin.Value < 0 || end.Value <= begin.Value || IsAuxiliary(paragraph)) continue;

            var fragments = new List<TextFragment>();
            var inlineTranslations = new List<string>();
            var inlineRomanizations = new List<string>();
            var invalidWordTiming = false;
            ReadInline(paragraph, null, begin.Value, end.Value, fragments, inlineTranslations, inlineRomanizations, ref invalidWordTiming);
            var (text, words) = NormalizeFragments(fragments);
            if (string.IsNullOrWhiteSpace(text)) continue;

            var key = AttributeValue(paragraph, "key") ?? paragraph.Attribute(XNamespace.Xml + "id")?.Value;
            var translation = key is not null && translations.TryGetValue(key, out var matched)
                ? matched : inlineTranslations.FirstOrDefault();
            var romanization = inlineRomanizations.Count > 0
                ? CollapseWhitespace(string.Join(' ', inlineRomanizations))
                : key is not null && romanizationsByKey.TryGetValue(key, out var keyedRomanization)
                    ? keyedRomanization
                    : romanizationsByBegin.TryGetValue(begin.Value, out var timedRomanization) ? timedRomanization : null;
            var line = new LyricsLine(
                Begin: begin.Value,
                End: end.Value,
                Text: text,
                SongPart: ResolveSongPart(paragraph),
                Words: invalidWordTiming ? null : words,
                Translation: translation,
                Romanization: romanization);
            if (line.Words is not null && !LyricWordTiming.IsUsable(line)) line = line with { Words = null };
            lines.Add(line);
        }

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
        if (string.IsNullOrWhiteSpace(rawValue)) return null;
        try
        {
            var value = TimeParser.ParseSeconds(rawValue);
            return double.IsFinite(value) ? value : null;
        }
        catch (Exception error) when (error is FormatException or OverflowException)
        {
            // An optional/word timestamp must not discard an otherwise usable native line.
            return null;
        }
    }

    private static string? AttributeValue(XElement element, string localName) => element.Attributes()
        .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value;

    private static bool HasRole(XElement element, string role) => AttributeValue(element, "role")?.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Contains(role, StringComparer.OrdinalIgnoreCase) == true;

    private static bool IsRomanizationRole(XElement element) => HasRole(element, "x-roman") || HasRole(element, "x-romanization");

    private static bool IsHiddenAuxiliary(XElement element) => HasRole(element, "x-bg")
        || IsRomanizationRole(element)
        || AttributeValue(element, "ruby") is "text" or "textContainer";

    private static bool IsAuxiliary(XElement element) => IsHiddenAuxiliary(element) || HasRole(element, "x-translation");

    private static (Dictionary<string, string> ByKey, Dictionary<double, string> ByBegin) ReadRomanizations(
        IEnumerable<XElement> paragraphs)
    {
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var byBegin = new Dictionary<double, string>();
        foreach (var paragraph in paragraphs)
        {
            if (!IsRomanizationRole(paragraph)) continue;
            var content = PlainContent(paragraph);
            if (string.IsNullOrWhiteSpace(content)) continue;
            var key = AttributeValue(paragraph, "key") ?? paragraph.Attribute(XNamespace.Xml + "id")?.Value;
            if (key is not null) byKey.TryAdd(key, content);
            var begin = TryParseTimeAttribute(paragraph, "begin");
            if (begin.HasValue) byBegin.TryAdd(begin.Value, content);
        }
        return (byKey, byBegin);
    }

    private static Dictionary<string, string> ReadTranslations(XDocument xml)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        // Observed Apple syllable-lyrics: iTunesMetadata/translations/translation/text[@for],
        // keyed by the paragraph's itunes:key. Keep the first nonempty locale in file order;
        // the present model holds one translation, not an arbitrary list of target languages.
        foreach (var metadata in xml.Descendants().Where(element => element.Name.LocalName == "iTunesMetadata"))
        foreach (var container in metadata.Descendants().Where(element => element.Name.LocalName == "translations"))
        foreach (var translation in container.Elements().Where(element => element.Name.LocalName == "translation"))
        foreach (var text in translation.Descendants().Where(element => element.Name.LocalName == "text"))
        {
            var key = text.Attribute("for")?.Value;
            var value = PlainContent(text);
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value)) result.TryAdd(key, value);
        }
        return result;
    }

    private static string PlainContent(XElement container)
    {
        var content = new StringBuilder();
        void Read(XElement parent)
        {
            foreach (var node in parent.Nodes())
            {
                if (node is XText text) content.Append(text.Value);
                else if (node is XElement child && !IsAuxiliary(child))
                {
                    if (child.Name.LocalName == "br") content.Append(' ');
                    else Read(child);
                }
            }
        }
        Read(container);
        return CollapseWhitespace(content.ToString());
    }

    private static void ReadInline(XElement parent, WordTiming? inherited, double lineBegin, double lineEnd,
        List<TextFragment> fragments, List<string> translations, List<string> romanizations, ref bool invalidTiming)
    {
        foreach (var node in parent.Nodes())
        {
            if (node is XText text)
            {
                fragments.Add(new TextFragment(text.Value, inherited));
                continue;
            }
            if (node is not XElement child) continue;
            if (IsRomanizationRole(child))
            {
                var romanization = PlainContent(child);
                if (!string.IsNullOrWhiteSpace(romanization)) romanizations.Add(romanization);
                continue;
            }
            if (IsHiddenAuxiliary(child)) continue;
            if (HasRole(child, "x-translation"))
            {
                var translation = PlainContent(child);
                if (!string.IsNullOrWhiteSpace(translation)) translations.Add(translation);
                continue;
            }
            if (child.Name.LocalName == "br")
            {
                fragments.Add(new TextFragment(" ", inherited));
                continue;
            }
            var timing = inherited;
            if (child.Attribute("begin") is not null || child.Attribute("end") is not null)
            {
                var begin = TryParseTimeAttribute(child, "begin");
                var end = TryParseTimeAttribute(child, "end");
                // Apple's native word clocks are absolute, not offsets from p.begin.
                if (begin.HasValue && end.HasValue && begin.Value >= lineBegin - 0.000001
                    && end.Value <= lineEnd + 0.000001 && end.Value >= begin.Value)
                    timing = new WordTiming(begin.Value, end.Value);
                else
                {
                    invalidTiming = true;
                    timing = null;
                }
            }
            ReadInline(child, timing, lineBegin, lineEnd, fragments, translations, romanizations, ref invalidTiming);
        }
    }

    private static (string Text, IReadOnlyList<LyricWord>? Words) NormalizeFragments(List<TextFragment> fragments)
    {
        var display = new StringBuilder();
        var words = new List<WordBuilder>();
        WordBuilder? currentWord = null;
        WordTiming? whitespaceTiming = null;
        var whitespace = false;
        void Append(char value, WordTiming? timing)
        {
            display.Append(value);
            if (timing is null)
            {
                currentWord = null;
                return;
            }
            if (currentWord is null || !ReferenceEquals(currentWord.Timing, timing))
            {
                currentWord = new WordBuilder(timing);
                words.Add(currentWord);
            }
            currentWord.Text.Append(value);
        }
        foreach (var fragment in fragments)
        foreach (var character in fragment.Text)
        {
            if (char.IsWhiteSpace(character))
            {
                // The surviving normalized separator keeps its source ownership. In particular,
                // a raw text node between spans must not inherit either neighbour's word clock.
                if (!whitespace) whitespaceTiming = fragment.Timing;
                whitespace = true;
                continue;
            }
            if (whitespace && display.Length > 0)
                Append(' ', whitespaceTiming);
            Append(character, fragment.Timing);
            whitespace = false;
            whitespaceTiming = null;
        }
        var text = display.ToString();
        var timedWords = words.Where(word => word.Text.Length > 0)
            .Select(word => new LyricWord(word.Timing.Begin, word.Timing.End, word.Text.ToString())).ToArray();
        // Untimed separators remain only in Text; real missing lyric content is rejected by
        // LyricWordTiming.IsUsable after constructing the line. Do not stretch source span clocks.
        return (text, timedWords.Length > 0 ? timedWords : null);
    }

    private sealed record WordTiming(double Begin, double End);
    private sealed record TextFragment(string Text, WordTiming? Timing);
    private sealed class WordBuilder(WordTiming timing)
    {
        public WordTiming Timing { get; } = timing;
        public StringBuilder Text { get; } = new();
    }

    private static string? ResolveSongPart(XElement paragraph)
    {
        static string? FindSongPart(XElement element)
        {
            return element.Attributes()
                .FirstOrDefault(attribute =>
                    string.Equals(attribute.Name.LocalName, "songPart", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(attribute.Name.LocalName, "song-part", StringComparison.OrdinalIgnoreCase))
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

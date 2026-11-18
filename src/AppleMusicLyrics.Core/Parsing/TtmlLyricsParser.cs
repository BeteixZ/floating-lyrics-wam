using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AppleMusicLyrics.Core.Matching;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Sync;

namespace AppleMusicLyrics.Core.Parsing;

public sealed class TtmlLyricsParser
{
    private static readonly XName AgentAttribute = XName.Get("agent", "http://www.w3.org/ns/ttml#metadata");

    public LyricsDocument ParseLyricsJson(string jsonText, string sourceFile)
    {
        using var document = JsonDocument.Parse(jsonText);
        var root = document.RootElement;

        var (resource, attributes) = ResolvePayload(root);
        var ttml = NonEmptyString(attributes, "ttmlLocalizations") ?? NonEmptyString(attributes, "ttml")
            ?? throw new InvalidOperationException("Lyrics JSON is missing a supported TTML payload.");
        var playParams = attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("playParams", out var parameters)
            ? parameters : default;
        var playCatalogId = ReadCatalogId(playParams, "catalogId");
        var resourceCatalogId = ReadCatalogId(resource, "id");
        string? lyricsId = NonEmptyString(root, "lyricsId") ?? NonEmptyString(resource, "lyricsId")
            ?? NonEmptyString(playParams, "id")
            ?? (playCatalogId is long trackId ? CatalogLyricsId.FromCatalogId(trackId) : NonEmptyString(playParams, "catalogId"))
            ?? (resourceCatalogId is long resourceId ? CatalogLyricsId.FromCatalogId(resourceId) : NonEmptyString(resource, "id"));
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
            NativeOffsetSeconds: parsed.NativeOffsetSeconds)
        {
            CatalogTrackId = playCatalogId ?? resourceCatalogId,
        };
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

    private static long? ReadCatalogId(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value)) return null;
        var id = value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number
            : value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.None,
                CultureInfo.InvariantCulture, out number) ? number : 0;
        return id > 0 ? id : null;
    }

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
        var transliterations = ReadTransliterations(xml);
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
            var backgroundVocals = new List<LyricsLine>();
            var invalidWordTiming = false;
            ReadInline(paragraph, null, begin.Value, end.Value, fragments, inlineTranslations, inlineRomanizations,
                ref invalidWordTiming, backgroundVocals);
            var (text, words) = NormalizeFragments(fragments);
            if (string.IsNullOrWhiteSpace(text)) continue;

            var key = AttributeValue(paragraph, "key") ?? paragraph.Attribute(XNamespace.Xml + "id")?.Value;
            var translation = key is not null && translations.TryGetValue(key, out var matched)
                ? matched : inlineTranslations.FirstOrDefault();
            var romanization = inlineRomanizations.Count > 0
                ? CollapseWhitespace(string.Join(' ', inlineRomanizations))
                : key is not null && romanizationsByKey.TryGetValue(key, out var keyedRomanization)
                    ? keyedRomanization
                    : key is not null && transliterations.TryGetValue(key, out var transliteration)
                        ? transliteration : romanizationsByBegin.GetValueOrDefault(begin.Value);
            var line = new LyricsLine(
                Begin: begin.Value,
                End: end.Value,
                Text: text,
                SongPart: ResolveSongPart(paragraph),
                Words: invalidWordTiming ? null : words,
                Translation: translation,
                Romanization: romanization)
            {
                VocalistId = ReadVocalistId(paragraph),
                BackgroundVocals = backgroundVocals.Count > 0 ? backgroundVocals.ToArray() : null,
            };
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

    private static string? ReadVocalistId(XElement paragraph)
    {
        var agent = paragraph.AncestorsAndSelf().Attributes(AgentAttribute).FirstOrDefault()?.Value.Trim();
        // An IDREFS list describes a shared vocal line. Only a single agent identifies a distinct part.
        return agent is { Length: > 0 } && !agent.Any(char.IsWhiteSpace) ? agent : null;
    }

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
        // Observed Apple syllable-lyrics: iTunesMetadata/translations/translation/text[@for],
        // keyed by the paragraph's itunes:key. Keep the first nonempty locale in file order;
        // the present model holds one translation, not an arbitrary list of target languages.
        return ReadKeyedText(ReadMetadataGroups(xml, "translations", "translation"));
    }

    private static Dictionary<string, string> ReadTransliterations(XDocument xml)
    {
        // A TTML document can carry multiple reading tracks, e.g. kana and romaji.
        // Use Latin-script tracks first and accept untagged transliterations as a fallback.
        var tracks = ReadMetadataGroups(xml, "transliterations", "transliteration")
            .Select(group => (Group: group, Language: group.Attribute(XNamespace.Xml + "lang")?.Value))
            .Select(track => (track.Group, Priority: string.IsNullOrWhiteSpace(track.Language) ? 1
                : track.Language.Split('-').Contains("Latn", StringComparer.OrdinalIgnoreCase) ? 0 : 2))
            .Where(track => track.Priority < 2)
            .OrderBy(track => track.Priority)
            .Select(track => track.Group);
        return ReadKeyedText(tracks);
    }

    private static IEnumerable<XElement> ReadMetadataGroups(XDocument xml, string containerName, string groupName) =>
        xml.Descendants().Where(element => element.Name.LocalName == "iTunesMetadata")
            .SelectMany(metadata => metadata.Descendants().Where(element => element.Name.LocalName == containerName))
            .SelectMany(container => container.Elements().Where(element => element.Name.LocalName == groupName));

    private static Dictionary<string, string> ReadKeyedText(IEnumerable<XElement> groups)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in groups)
            foreach (var text in group.Descendants().Where(element => element.Name.LocalName == "text"))
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
        List<TextFragment> fragments, List<string>? translations, List<string>? romanizations,
        ref bool invalidTiming, List<LyricsLine>? backgroundVocals = null)
    {
        foreach (var node in parent.Nodes())
        {
            if (node is XText text)
            {
                fragments.Add(new TextFragment(text.Value, inherited));
                continue;
            }
            if (node is not XElement child) continue;
            if (HasRole(child, "x-bg"))
            {
                if (backgroundVocals is not null && ReadBackgroundVocal(child, lineBegin, lineEnd) is { } vocal)
                    backgroundVocals.Add(vocal);
                continue;
            }
            if (IsRomanizationRole(child))
            {
                var romanization = PlainContent(child);
                if (!string.IsNullOrWhiteSpace(romanization)) romanizations?.Add(romanization);
                continue;
            }
            if (IsHiddenAuxiliary(child)) continue;
            if (HasRole(child, "x-translation"))
            {
                var translation = PlainContent(child);
                if (!string.IsNullOrWhiteSpace(translation)) translations?.Add(translation);
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
            ReadInline(child, timing, lineBegin, lineEnd, fragments, translations, romanizations,
                ref invalidTiming, backgroundVocals);
        }
    }

    private static LyricsLine? ReadBackgroundVocal(XElement element, double parentBegin, double parentEnd)
    {
        var begin = TryParseTimeAttribute(element, "begin");
        var end = TryParseTimeAttribute(element, "end");
        if ((begin ?? parentBegin) < 0 || (end ?? parentEnd) <= (begin ?? parentBegin))
            (begin, end) = (null, null);

        var fragments = new List<TextFragment>();
        var invalidTiming = false;
        ReadInline(element, null, begin ?? parentBegin, end ?? parentEnd, fragments, null, null, ref invalidTiming);
        UnwrapBackgroundVocal(fragments);
        var (text, words) = NormalizeFragments(fragments);
        if (text.Length == 0) return null;

        // Apple can omit the wrapper's timestamps and time only its constituent syllables.
        var line = new LyricsLine(
            begin ?? words?.Min(word => word.Begin) ?? parentBegin,
            end ?? words?.Max(word => word.End) ?? parentEnd,
            text, Words: invalidTiming ? null : words);
        if (line.End <= line.Begin) line = line with { Begin = parentBegin, End = parentEnd };
        return line.Words is not null && !LyricWordTiming.IsUsable(line) ? line with { Words = null } : line;
    }

    private static void UnwrapBackgroundVocal(List<TextFragment> fragments)
    {
        var first = fragments.FindIndex(fragment => !string.IsNullOrWhiteSpace(fragment.Text));
        if (first < 0) return;
        var last = fragments.FindLastIndex(fragment => !string.IsNullOrWhiteSpace(fragment.Text));
        var start = fragments[first].Text.TrimStart();
        var end = fragments[last].Text.TrimEnd();
        if ((start[0], end[^1]) is not (('(', ')') or ('（', '）'))) return;

        // The wrapper may be untimed or embedded inside the first and last timed fragments.
        fragments[first] = fragments[first] with { Text = start[1..] };
        fragments[last] = fragments[last] with { Text = fragments[last].Text.TrimEnd()[..^1] };
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

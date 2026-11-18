using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AppleMusicLyrics.Core.Matching;
using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Core.Parsing;

/// <summary>Removes provider metadata before a document reaches the display timeline.</summary>
public static class LyricsContentFilter
{
    // Filter every source, including old disk-cache hits, once per immutable document.
    // Keep clean documents and individual lyric lines by reference, and do no work per frame.
    private static readonly ConditionalWeakTable<LyricsDocument,
        ConcurrentDictionary<(string Title, string Artist), LyricsDocument>> Cleaned = new();
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
    private const string Section = @"(?:verse|(?:pre|post)[\s-]*chorus|pre|chorus|hook|bridge|intro|outro|refrain|副歌|主歌|桥段|橋段|主|副|桥|橋|引)\s*\d*";
    private static readonly Regex SectionLabel = new(
        @"^\s*(?:[\[【（(]\s*(?:" + Section + @"|part\s+(?:[ivx]+|\d+)|第.+部分)(?:\s*[:：].*)?\s*[\]】）)]|" +
        Section + @"\s*[:：])\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex TitleSeparator = new(@"\s+[-–—]\s+", RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex Parentheses = new(@"\s*[（(][^()（）]*[)）]\s*", RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex TrailingTitleGroup = new(@"\s*[（(\[][^()（）\[\]]*[)）\]]\s*$", RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex TitleQualifier = new(@"(?:remastered?\d*|remix|bonustrack|explicit|clean|originalmix)$", RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex TitleSuffix = new(@"^(?:feat(?:uring)?.+|(?:19|20)\d{2}|(?:remastered?|remix|bonustrack|explicit|clean|originalmix)\d*)$", RegexOptions.CultureInvariant, MatchTimeout);

    public static LyricsDocument Clean(LyricsDocument document, PlayerState? player = null)
    {
        var identity = (MetadataMatching.Normalize(player?.Title), MetadataMatching.Normalize(player?.Artist));
        return Cleaned.GetValue(document, static _ => new()).GetOrAdd(identity, key => Filter(document, key));
    }

    private static LyricsDocument Filter(LyricsDocument source, (string Title, string Artist) identity)
    {
        var artists = identity.Artist;
        var headers = new HashSet<int>();
        foreach (var index in new[] { 0, source.Lines.Count - 1 }.Distinct())
        {
            if (index < 0 || index >= source.Lines.Count) continue;
            if (IsTitleHeader(source.Lines[index].Text, identity, out var headerArtists))
            {
                headers.Add(index);
                artists += headerArtists;
            }
        }
        List<LyricsLine>? lines = null;
        for (var index = 0; index < source.Lines.Count; index++)
        {
            var line = source.Lines[index];
            var remove = LyricsTranslationFilter.IsCreditOrNotice(line.Text)
                || headers.Contains(index) || IsSectionOrPerformerLabel(line.Text, artists);
            var translation = LyricsTranslationFilter.Clean(line.Translation);
            if (lines is null && (remove || translation != line.Translation))
            {
                lines = new List<LyricsLine>(source.Lines.Count);
                for (var previous = 0; previous < index; previous++) lines.Add(source.Lines[previous]);
            }
            if (lines is not null && !remove)
                lines.Add(translation == line.Translation ? line : line with { Translation = translation });
        }
        // Never recompute starts/ends after deleting a credit row: the sung timeline stays intact.
        return lines is null ? source : source with { Lines = lines.ToArray() };
    }

    private static bool IsTitleHeader(string text, (string Title, string Artist) identity, out string headerArtists)
    {
        headerArtists = string.Empty;
        if (identity.Title.Length == 0 || identity.Artist.Length < 3) return false;
        // Only an opening/closing header agreeing with both track fields is metadata. A dash in an
        // ordinary lyric, or a sung repetition of the title, is never enough to remove it.
        foreach (Match separator in TitleSeparator.Matches(text))
        {
            var parts = new[] { text[..separator.Index], text[(separator.Index + separator.Length)..] };
            for (var titleIndex = 0; titleIndex < 2; titleIndex++)
            {
                var artist = MetadataMatching.Normalize(Parentheses.Replace(parts[1 - titleIndex], string.Empty));
                if (HeaderTitleMatches(parts[titleIndex], identity.Title)
                    && artist.Length >= 3 && (artist.Contains(identity.Artist, StringComparison.Ordinal)
                        || identity.Artist.Contains(artist, StringComparison.Ordinal)))
                {
                    headerArtists = artist;
                    return true;
                }
            }
        }
        return false;
    }

    private static bool HeaderTitleMatches(string text, string title)
    {
        // Strip suffix groups one at a time: "Blow Your Mind (Mwah) (Explicit)" must keep
        // "Mwah" when dropping "Explicit". This affects header removal only, not song matching.
        for (var remaining = text; remaining.Length > 0;)
        {
            var normalized = MetadataMatching.Normalize(remaining);
            if (Agrees(normalized) || Agrees(TitleQualifier.Replace(normalized, string.Empty))) return true;
            var shorter = TrailingTitleGroup.Replace(remaining, string.Empty);
            if (shorter == remaining) return false;
            remaining = shorter;
        }
        return false;

        bool Agrees(string candidate) => candidate.Length > 0 && (candidate == title
            || (title.StartsWith(candidate, StringComparison.Ordinal) && TitleSuffix.IsMatch(title[candidate.Length..]))
            || (candidate.StartsWith(title, StringComparison.Ordinal) && TitleSuffix.IsMatch(candidate[title.Length..])));
    }

    private static bool IsSectionOrPerformerLabel(string text, string artists)
    {
        if (SectionLabel.IsMatch(text)) return true;
        var trimmed = text.Trim();
        if (!trimmed.EndsWith(':') && !trimmed.EndsWith('：')) return false;
        var name = MetadataMatching.Normalize(trimmed);
        // A standalone artist marker is auxiliary text; an inline marker followed by sung
        // words ("Macy Gray: Dangerous love") stays intact, including its word timing.
        return name.Length >= 3 && artists.Contains(name, StringComparison.Ordinal);
    }
}

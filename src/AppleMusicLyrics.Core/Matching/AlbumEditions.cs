using System.Text.RegularExpressions;

namespace AppleMusicLyrics.Core.Matching;

/// <summary>
/// Recognises reissues of an album, such as "Butterfly: 25th Anniversary Expanded Edition",
/// "E=MC2 (Deluxe Version)", or "In the Lonely Hour (10th Anniversary Edition) [Deluxe]".
/// A reissue usually carries the original album's recordings, but lyric services often attach
/// richer lyrics only to the original release.
/// </summary>
public static class AlbumEditions
{
    // A trailing bracketed group, for example "(Deluxe)", "[Bonus Track Version]" or "（豪华版）".
    private static readonly Regex BracketedSuffix = new(
        @"\s*(?:\((?<tag>[^()]+)\)|\[(?<tag>[^\[\]]+)\]|（(?<tag>[^（）]+)）|【(?<tag>[^【】]+)】)\s*\z",
        RegexOptions.CultureInvariant);

    // A trailing subtitle after a colon or a spaced dash, for example ": 25th Anniversary Expanded Edition".
    private static readonly Regex SubtitleSuffix = new(
        @"\s*(?::|：|\s[-–—]\s)\s*(?<tag>[^:：()\[\]（）【】]+?)\s*\z",
        RegexOptions.CultureInvariant);

    // Words that make a suffix a reissue marker. "Version" alone is not enough: "Taylor's Version"
    // is a re-recording, so it only counts beside a reissue qualifier.
    private static readonly Regex EditionMarker = new(
        @"(?<![\p{L}])(?:deluxe|expanded|anniversary|remaster(?:ed)?|reissue|re-?release|repackage|"
        + @"special|collector'?s|legacy|platinum|bonus|tour|complete|definitive|ultimate|"
        + @"(?:\d+(?:st|nd|rd|th)|twenty|thirty|forty|fifty)\s+(?:anniversary|years?)|"
        + @"[ée]dition|edici[oó]n|ausgabe|"
        + @"(?:international|japan(?:ese)?|uk|us|eu|european|asian|video|standard|digital)\s+version)(?![\p{L}])"
        + @"|豪华版|豪華版|纪念版|紀念版|周年|週年|特别版|特別版|典藏版|珍藏版|加长版|加長版|完整版|再版|"
        + @"デラックス|エディション|記念盤|完全版",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Markers of a different recording or a different text. Such albums are never folded.
    private static readonly Regex DifferentRecording = new(
        @"(?<![\p{L}])(?:live|remix(?:es|ed)?|acoustic|instrumental|karaoke|a\s*cappella|acapella|"
        + @"unplugged|orchestral|demos?|sped\s*up|slowed|chopped|screwed|taylor'?s\s+version|"
        + @"clean|edited|commentary)(?![\p{L}])|现场|現場|伴奏|混音|翻唱",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const int MaxSuffixes = 3;

    /// <summary>
    /// The album name without its reissue suffixes, or null when <paramref name="album"/> is not
    /// recognisably a reissue. Album names that merely contain such a word, like Sade's
    /// "Love Deluxe", are left alone: only a bracketed or separated suffix is removed.
    /// </summary>
    public static string? GetOriginalAlbum(string? album)
    {
        if (string.IsNullOrWhiteSpace(album) || DifferentRecording.IsMatch(album)) return null;

        var remaining = album.Trim();
        for (var removed = 0; removed < MaxSuffixes; removed++)
        {
            var suffix = BracketedSuffix.Match(remaining);
            if (!suffix.Success || !EditionMarker.IsMatch(suffix.Groups["tag"].Value))
                suffix = SubtitleSuffix.Match(remaining);
            if (!suffix.Success || !EditionMarker.IsMatch(suffix.Groups["tag"].Value)) break;

            var shorter = remaining[..suffix.Index].TrimEnd();
            if (MetadataMatching.Normalize(shorter).Length == 0) break;
            remaining = shorter;
        }

        return MetadataMatching.Normalize(remaining) == MetadataMatching.Normalize(album) ? null : remaining;
    }
}

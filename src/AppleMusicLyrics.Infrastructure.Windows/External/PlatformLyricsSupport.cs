using System.Text.RegularExpressions;
using AppleMusicLyrics.Core.Matching;
using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Infrastructure.Windows.External;

internal static class PlatformLyricsSupport
{
    // Recording variants have different timelines, even when most of the title agrees.
    private static readonly Regex VariantPattern = new(
        @"(?<![a-z])(live|remix|instrumental|karaoke|acoustic|demo|intro|extended|version|sped\s*up|slowed)(?![a-z])|现场|伴奏|混音",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private const string FeaturingPrefix = @"(?:feat(?:uring)?\.?|ft\.?)";
    private static readonly Regex BracketedFeaturing = new(
        @"\(\s*" + FeaturingPrefix + @"\s+(?<guest>[^()]+)\)|\[\s*" + FeaturingPrefix
        + @"\s+(?<guest>[^\[\]]+)\]|（\s*" + FeaturingPrefix + @"\s+(?<guest>[^（）]+)）",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TrailingFeaturing = new(
        @"(?:\s*[-–—]\s*|\s+)" + FeaturingPrefix + @"\s+(?<guest>[^()\[\]（）]+)(?=\s*[(\[（]|\z)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string SearchTerm(PlayerState player) => $"{ParseTitle(player.Title).Title} {player.Artist?.Trim()}";

    public static int ScoreCandidate(PlayerState player, string? title, string? artist, string? album, double? duration)
    {
        var playerTitle = ParseTitle(player.Title);
        var candidateTitle = ParseTitle(title);
        var titleScore = MetadataMatching.Score(playerTitle.Title, candidateTitle.Title, exact: 40, partial: 18);
        var artistScore = MetadataMatching.Score(player.Artist, artist, exact: 30, partial: 14);
        if (artistScore == 0 && artist is not null)
        {
            artistScore = artist.Split([',', '/', '&', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(part => MetadataMatching.Score(player.Artist, part, exact: 30, partial: 14))
                .DefaultIfEmpty(0).Max();
        }
        if (titleScore == 0 || artistScore == 0 || !SameVariants(player.Title, title)
            || (playerTitle.Guests.Count > 0 && candidateTitle.Guests.Count > 0
                && !playerTitle.Guests.SetEquals(candidateTitle.Guests)))
        {
            return int.MinValue;
        }

        var durationScore = 0;
        if (double.IsFinite(player.Duration) && player.Duration > 0 && duration is > 0 && double.IsFinite(duration.Value))
        {
            var delta = Math.Abs(player.Duration - duration.Value);
            if (delta > 15)
            {
                return int.MinValue;
            }
            durationScore = 80 - (int)Math.Round(delta * 8);
        }

        return titleScore + artistScore + MetadataMatching.Score(player.Album, album, exact: 8, partial: 4) + durationScore;
    }

    private static (string Title, HashSet<string> Guests) ParseTitle(string? title)
    {
        var guests = new HashSet<string>(StringComparer.Ordinal);
        string RemoveCredit(Match match)
        {
            var guest = match.Groups["guest"].Value.Trim();
            // Only explicit guest credits are metadata. Ambiguous suffixes containing recording
            // markers stay intact, so removing a credit can never erase a live/remix/version tag.
            if (VariantPattern.IsMatch(guest) || MetadataMatching.Normalize(guest) is not { Length: > 0 } normalized)
                return match.Value;
            guests.Add(normalized);
            return " ";
        }

        var remaining = BracketedFeaturing.Replace(title ?? string.Empty, RemoveCredit);
        remaining = TrailingFeaturing.Replace(remaining, RemoveCredit);
        return (Regex.Replace(remaining, @"\s+", " ").Trim(), guests);
    }

    private static bool SameVariants(string? left, string? right)
    {
        var leftVariants = VariantPattern.Matches(left ?? string.Empty)
            .Select(match => MetadataMatching.Normalize(match.Value)).ToHashSet(StringComparer.Ordinal);
        var rightVariants = VariantPattern.Matches(right ?? string.Empty)
            .Select(match => MetadataMatching.Normalize(match.Value)).ToHashSet(StringComparer.Ordinal);
        return leftVariants.SetEquals(rightVariants);
    }
}

using System.Text.RegularExpressions;
using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Core.Matching;

/// <summary>
/// Comparing track metadata that came from two different catalogues, where the same song is written
/// "S.E.X.", "S.E.X", and "S E X" depending on who typed it in.
/// </summary>
public static class MetadataMatching
{
    private static readonly Regex EvidenceTokenPattern = new(
        @"[\p{L}\p{N}]+|\*{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> VocalizationTokens = new(StringComparer.Ordinal)
    {
        "ah", "aha", "eh", "ha", "hey", "hm", "hmm", "la", "me", "mm", "mmm",
        "na", "oh", "ooh", "uh", "uhh", "woo", "yeah", "yo", "you",
    };

    /// <summary>
    /// Strips everything that is not a letter or digit and lowercases the rest, so punctuation and
    /// spacing differences stop mattering.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    /// <summary>
    /// Scores how well two metadata fields agree: <paramref name="exact"/> for an exact match after
    /// normalisation, <paramref name="partial"/> when one contains the other and they are close
    /// enough in length, 0 otherwise.
    /// </summary>
    public static int Score(string? left, string? right, int exact, int partial)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        if (normalizedLeft.Length == 0 || normalizedRight.Length == 0)
        {
            return 0;
        }

        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal))
        {
            return exact;
        }

        // Containment alone is far too generous once punctuation and spaces are stripped — "Song"
        // sits inside "A Completely Different Song". Only treat it as a partial match when the two
        // are close enough in length that the extra text can only be a suffix like "(Remastered)".
        var shorter = Math.Min(normalizedLeft.Length, normalizedRight.Length);
        var longer = Math.Max(normalizedLeft.Length, normalizedRight.Length);
        if ((double)shorter / longer < 0.6)
        {
            return 0;
        }

        return normalizedRight.Contains(normalizedLeft, StringComparison.Ordinal)
            || normalizedLeft.Contains(normalizedRight, StringComparison.Ordinal)
            ? partial
            : 0;
    }

    /// <summary>
    /// Separates combined artist and album strings. Apple Music on Windows packages
    /// "Artist — Album" (using em dash U+2014, en dash U+2013, or spaced hyphen) into the SMTC Artist
    /// property and leaves AlbumTitle empty.
    /// </summary>
    public static (string Artist, string? Album) ParseArtistAndAlbum(string? rawArtist, string? rawAlbum)
    {
        if (string.IsNullOrWhiteSpace(rawArtist))
        {
            return (string.Empty, string.IsNullOrWhiteSpace(rawAlbum) ? null : rawAlbum.Trim());
        }

        var trimmedArtist = rawArtist.Trim();
        var trimmedAlbum = string.IsNullOrWhiteSpace(rawAlbum) ? null : rawAlbum.Trim();

        var delimiters = new[] { " — ", " – ", " —", "— ", "—", " –", "– ", "–", " - " };
        foreach (var delimiter in delimiters)
        {
            var idx = trimmedArtist.IndexOf(delimiter, StringComparison.Ordinal);
            if (idx > 0)
            {
                var artistPart = trimmedArtist[..idx].Trim();
                var albumPart = trimmedArtist[(idx + delimiter.Length)..].Trim();

                if (!string.IsNullOrEmpty(artistPart) && !string.IsNullOrEmpty(albumPart))
                {
                    return (artistPart, trimmedAlbum ?? albumPart);
                }
            }
        }

        return (trimmedArtist, trimmedAlbum);
    }

    /// <summary>
    /// Classifies title evidence while retaining word boundaries. A single-word title is only weak
    /// evidence because ordinary lyric prose frequently contains words such as "ocean", "home",
    /// "me", or "you". Multi-word titles must occur as a contiguous phrase in one line.
    /// </summary>
    public static TitleEvidenceStrength GetTitleEvidence(string? title, IEnumerable<string> lines)
    {
        var titleTokens = TokenizeForEvidence(title, includeCensoredWildcard: false);
        if (titleTokens.Count == 0)
        {
            return TitleEvidenceStrength.None;
        }

        var best = TitleEvidenceStrength.None;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var lineTokens = TokenizeForEvidence(line, includeCensoredWildcard: true);
            if (ContainsTokenPhrase(lineTokens, titleTokens))
            {
                return titleTokens.Count == 1
                    ? TitleEvidenceStrength.Weak
                    : TitleEvidenceStrength.Strong;
            }

            // Some providers censor only the distinctive end of a longer title. Preserve this as
            // weak evidence for diagnostics, but never let it select a cache file by itself.
            if (titleTokens.Count >= 3 &&
                ContainsTokenPhrase(lineTokens, titleTokens.TakeLast(2).ToArray()))
            {
                best = TitleEvidenceStrength.Weak;
            }
        }

        return best;
    }

    public static bool DocumentContainsTitle(string? title, IEnumerable<string> lines)
    {
        return GetTitleEvidence(title, lines) != TitleEvidenceStrength.None;
    }

    private static IReadOnlyList<string> TokenizeForEvidence(string? value, bool includeCensoredWildcard)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return EvidenceTokenPattern.Matches(value)
            .Select(match => match.Value.StartsWith('*') && includeCensoredWildcard
                ? "*"
                : match.Value.ToLowerInvariant())
            .Where(token => includeCensoredWildcard || token != "*")
            .ToArray();
    }

    private static bool ContainsTokenPhrase(IReadOnlyList<string> lineTokens, IReadOnlyList<string> titleTokens)
    {
        if (titleTokens.Count == 0 || lineTokens.Count < titleTokens.Count)
        {
            return false;
        }

        for (var start = 0; start <= lineTokens.Count - titleTokens.Count; start++)
        {
            var matched = true;
            for (var offset = 0; offset < titleTokens.Count; offset++)
            {
                var lineToken = lineTokens[start + offset];
                if (lineToken != "*" && !string.Equals(lineToken, titleTokens[offset], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Detects long synchronized documents that contain only a handful of repeated pronouns or
    /// vocal sounds. Community databases sometimes publish these as ordinary lyrics even when a
    /// track has no substantive lyric text (for example, a dozen repetitions of "Me, meyou").
    /// The thresholds are deliberately narrow so short choruses and normally repetitive songs are
    /// not rejected merely for reusing words.
    /// </summary>
    public static bool IsRepetitiveVocalizationOnly(string? title, IEnumerable<string> lines)
    {
        var nonEmptyLines = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .ToArray();
        if (nonEmptyLines.Length < 8)
        {
            return false;
        }

        var uniqueLines = nonEmptyLines
            .Select(Normalize)
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (uniqueLines.Length > 4)
        {
            return false;
        }

        var tokens = nonEmptyLines
            .SelectMany(line => line.Split(
                new[] { ' ', '\t', ',', '.', '!', '?', ';', ':', '\'', '"', '(', ')', '[', ']', '—', '-', '–', '/', '\\' },
                StringSplitOptions.RemoveEmptyEntries))
            .Select(Normalize)
            .Where(token => token.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (tokens.Length == 0 || tokens.Length > 4)
        {
            return false;
        }

        var normalizedTitle = Normalize(title);
        return tokens.All(token =>
            VocalizationTokens.Contains(token) ||
            (normalizedTitle.Length > 0 && string.Equals(token, normalizedTitle, StringComparison.Ordinal)));
    }
}

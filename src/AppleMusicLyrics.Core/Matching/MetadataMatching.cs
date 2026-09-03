using System.Text.RegularExpressions;

namespace AppleMusicLyrics.Core.Matching;

/// <summary>
/// Comparing track metadata that came from two different catalogues, where the same song is written
/// "S.E.X.", "S.E.X", and "S E X" depending on who typed it in.
/// </summary>
public static class MetadataMatching
{
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
    /// Tests whether the lyrics text contains sufficient evidence of the song title,
    /// accounting for punctuation, spacing, and explicit words censored with asterisks (e.g. "****").
    /// Evidence must occur within a single line rather than accumulating unrelated words across the entire document.
    /// </summary>
    public static bool DocumentContainsTitle(string? title, IEnumerable<string> lines)
    {
        var normalizedTitle = Normalize(title);
        if (normalizedTitle.Length < 2)
        {
            return false;
        }

        var titleWords = (title ?? string.Empty)
            .Split(new[] { ' ', '-', '—', '/', '(', ')', '[', ']', '\'', '"', ',', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(w => w.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // For censored titles (e.g. "New Nigga Now" -> words: "new", "nigga", "now")
        Regex? censoredPattern = null;
        if (titleWords.Length >= 2)
        {
            var wordPatterns = titleWords.Select(w => "(?:" + Regex.Escape(w) + @"|\*{2,})").ToArray();
            var fullPattern = string.Join(@"\s+", wordPatterns);
            censoredPattern = new Regex(fullPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        Regex? censoredSubPattern = null;
        if (titleWords.Length >= 3)
        {
            var lastTwo = string.Join(@"\s+", titleWords.TakeLast(2).Select(w => "(?:" + Regex.Escape(w) + @"|\*{2,})"));
            censoredSubPattern = new Regex(lastTwo, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var normalizedLine = Normalize(line);
            // 1. Direct normalized phrase in a single line (e.g. "I don't give a fuck, bitch, you better go, go get 'em" contains "gogetem")
            if (normalizedLine.Contains(normalizedTitle, StringComparison.Ordinal))
            {
                return true;
            }

            // 2. Censored line match in a single line (e.g. "A-E-I-O-U, **** now" matches "**** now")
            if (line.Contains('*'))
            {
                if (censoredPattern != null && censoredPattern.IsMatch(line))
                {
                    return true;
                }

                if (censoredSubPattern != null && censoredSubPattern.IsMatch(line))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

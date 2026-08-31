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
}

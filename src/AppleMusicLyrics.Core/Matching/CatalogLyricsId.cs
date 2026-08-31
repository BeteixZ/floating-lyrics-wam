using System.Globalization;

namespace AppleMusicLyrics.Core.Matching;

/// <summary>
/// Apple stamps every cached lyrics file with a lyricsId of "AP_&lt;catalog song id&gt;", so the file
/// already carries the exact identity of the track it belongs to. That makes an id comparison an
/// exact test where duration comparison is only ever a guess.
/// </summary>
public static class CatalogLyricsId
{
    private const string Prefix = "AP_";

    public static string FromCatalogId(long catalogId)
    {
        return Prefix + catalogId.ToString(CultureInfo.InvariantCulture);
    }

    public static long? ToCatalogId(string? lyricsId)
    {
        if (string.IsNullOrWhiteSpace(lyricsId) ||
            !lyricsId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return long.TryParse(
            lyricsId.AsSpan(Prefix.Length),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var catalogId)
            ? catalogId
            : null;
    }

    public static bool Matches(string? lyricsId, string? otherLyricsId)
    {
        return !string.IsNullOrWhiteSpace(lyricsId)
            && string.Equals(lyricsId, otherLyricsId, StringComparison.OrdinalIgnoreCase);
    }
}

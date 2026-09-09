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

        var value = lyricsId.AsSpan(Prefix.Length);
        var suffixIndex = value.IndexOf('-');
        if (suffixIndex >= 0)
        {
            var suffix = value[(suffixIndex + 1)..];
            if (suffixIndex == 0 || suffix.IsEmpty)
            {
                return null;
            }

            foreach (var character in suffix)
            {
                if (!char.IsLetterOrDigit(character) && character != '-')
                {
                    return null;
                }
            }

            value = value[..suffixIndex];
        }

        return long.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var catalogId)
            ? catalogId
            : null;
    }

    public static bool Matches(string? lyricsId, string? otherLyricsId)
    {
        if (string.IsNullOrWhiteSpace(lyricsId) || string.IsNullOrWhiteSpace(otherLyricsId))
        {
            return false;
        }

        var catalogId = ToCatalogId(lyricsId);
        var otherCatalogId = ToCatalogId(otherLyricsId);
        return catalogId.HasValue && otherCatalogId.HasValue
            ? catalogId.Value == otherCatalogId.Value
            : string.Equals(lyricsId, otherLyricsId, StringComparison.OrdinalIgnoreCase);
    }
}

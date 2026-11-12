using System.Globalization;
using System.Text;
using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Core.Sync;

/// <summary>Shared admission rules for word timings that can represent the displayed lyric text.</summary>
public static class LyricWordTiming
{
    /// <summary>
    /// Validates absolute word times and binds their fragments in display-text order. Untimed
    /// whitespace and punctuation are allowed, but omitted lyric content is not. Instant fragments
    /// are allowed alongside at least one positive-duration content fragment. Overlapping and
    /// out-of-order timestamps remain supported, as in existing external QRC documents;
    /// text order does not imply that one word must finish before the next one starts.
    /// </summary>
    public static bool IsUsable(LyricsLine line)
    {
        if (line is null || string.IsNullOrWhiteSpace(line.Text)
            || line.Words is not { Count: > 0 } words)
        {
            return false;
        }

        var hasTimedContent = false;
        foreach (var word in words)
        {
            if (word is null || string.IsNullOrEmpty(word.Text)
                || !double.IsFinite(word.Begin) || !double.IsFinite(word.End)
                || word.Begin < 0 || word.End < word.Begin)
            {
                return false;
            }
            if (word.End > word.Begin && HasContent(word.Text.AsSpan())) hasTimedContent = true;
        }
        if (!hasTimedContent) return false;

        var text = line.Text.Trim();
        // Preserve the former exact-concatenation admission, including external fragments that
        // own whitespace. The binding below additionally admits safe, untimed TTML separators.
        if (string.Equals(string.Concat(words.Select(word => word.Text)).Trim(), text, StringComparison.Ordinal))
        {
            return true;
        }

        var cursor = 0;
        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            // Providers can trim the display line while retaining outer spaces on their fragments.
            // Interior whitespace remains exact; only gaps outside a matched fragment may be skipped.
            var fragment = word.Text;
            if (index == 0) fragment = fragment.TrimStart();
            if (index == words.Count - 1) fragment = fragment.TrimEnd();
            if (fragment.Length == 0) continue;

            var start = text.IndexOf(fragment, cursor, StringComparison.Ordinal);
            if (start < 0 || !IsUntimedGap(text.AsSpan(cursor, start - cursor)))
            {
                return false;
            }

            cursor = start + fragment.Length;
        }

        return IsUntimedGap(text.AsSpan(cursor));
    }

    private static bool IsUntimedGap(ReadOnlySpan<char> gap)
    {
        foreach (var rune in gap.EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune) && !Rune.IsPunctuation(rune)) return false;
        }
        return true;
    }

    private static bool HasContent(ReadOnlySpan<char> text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune) && !Rune.IsPunctuation(rune) && !Rune.IsControl(rune)
                && Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format)
            {
                return true;
            }
        }
        return false;
    }
}

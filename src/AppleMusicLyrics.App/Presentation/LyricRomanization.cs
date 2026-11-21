using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using AppleMusicLyrics.Core.Models;
using Lucene.Net.Analysis.Ja;
using Lucene.Net.Analysis.Ja.TokenAttributes;
using Lucene.Net.Analysis.Ja.Util;
using Lucene.Net.Analysis.TokenAttributes;
using Lucene.Net.Util;

namespace AppleMusicLyrics.App.Presentation;

internal static class LyricRomanization
{
    private static readonly ConditionalWeakTable<LyricsDocument, SongReadings> Readings = new();

    public static string? Resolve(LyricsDocument? document, LyricsLine? line, bool enabled)
    {
        if (!enabled || document is null || line is null) return null;
        if (!string.IsNullOrWhiteSpace(line.Romanization)) return line.Romanization;

        var hasKana = ContainsKana(line.Text);
        if (!hasKana && !ContainsIdeographs(line.Text)) return null;
        var song = Readings.GetValue(document, static document => new SongReadings(document));
        return hasKana || song.IsJapanese ? song.Lines.GetOrAdd(line.Text, Generate) : null;
    }

    private static string? Generate(string text)
    {
        text = text.Normalize(NormalizationForm.FormKC);
        if (Read(text, out var unresolved) is { } kana) return Romanize(kana, text);
        // Lyrics typed on a Chinese input method often carry simplified characters in place of
        // the kanji (离れ for 離れ, 见 for 見), which the Japanese dictionary cannot read. Swap
        // only the characters of the words it could not read for their kanji and try again.
        var kanjiText = KanjiVariants.Replace(text, unresolved);
        return kanjiText != text && Read(kanjiText, out _) is { } retried ? Romanize(retried, text) : null;
    }

    private static string? Romanize(string kana, string text)
    {
        var romanized = RomanizeKana(kana).Trim();
        return !ContainsKana(romanized) && romanized != text && romanized.Any(char.IsLetter) ? romanized : null;
    }

    /// <summary>The line in kana, or null when a word with kanji has no known reading.</summary>
    private static string? Read(string text, out List<(int Start, int End)> unresolved)
    {
        unresolved = [];
        using var reader = new System.IO.StringReader(text);
        using var tokenizer = new JapaneseTokenizer(AttributeSource.AttributeFactory.DEFAULT_ATTRIBUTE_FACTORY,
            reader, null, discardPunctuation: true, JapaneseTokenizerMode.NORMAL);
        var term = tokenizer.AddAttribute<ICharTermAttribute>();
        var reading = tokenizer.AddAttribute<IReadingAttribute>();
        var partOfSpeech = tokenizer.AddAttribute<IPartOfSpeechAttribute>();
        var offset = tokenizer.AddAttribute<IOffsetAttribute>();

        var phoneticText = new StringBuilder();
        var cursor = 0;
        var previousWasJapanese = false;
        tokenizer.Reset();
        while (tokenizer.IncrementToken())
        {
            var surface = term.ToString();
            var isJapanese = ContainsKana(surface) || ContainsIdeographs(surface);
            var part = partOfSpeech.GetPartOfSpeech();
            var joinsPrevious = previousWasJapanese && isJapanese
                && (part?.StartsWith("助動詞", StringComparison.Ordinal) == true
                    || (surface is "て" or "で" && part == "助詞-接続助詞"));
            if (offset.StartOffset > cursor)
                phoneticText.Append(text.AsSpan(cursor, offset.StartOffset - cursor));
            else if (cursor > 0 && (isJapanese || previousWasJapanese) && !joinsPrevious)
                phoneticText.Append(' ');

            var pronunciation = part?.StartsWith("助詞-", StringComparison.Ordinal) == true && surface is "は" or "へ" or "を"
                ? reading.GetPronunciation() : reading.GetReading();
            if (isJapanese && pronunciation is null && ContainsIdeographs(surface))
                unresolved.Add((offset.StartOffset, offset.EndOffset));
            phoneticText.Append(isJapanese ? pronunciation ?? ToKatakana(surface) : surface);
            previousWasJapanese = isJapanese;
            cursor = offset.EndOffset;
        }
        tokenizer.End();
        phoneticText.Append(text.AsSpan(cursor));

        var kana = phoneticText.ToString();
        // An unresolved word can be a name or a creative reading. Avoid guessing its kanji.
        return unresolved.Count > 0 || ContainsIdeographs(kana) ? null : kana;
    }

    private static string ToKatakana(string text) => string.Create(text.Length, text, static (output, input) =>
    {
        for (var i = 0; i < input.Length; i++)
            output[i] = input[i] is >= '\u3041' and <= '\u3096' ? (char)(input[i] + 0x60) : input[i];
    });

    private static string RomanizeKana(string kana)
    {
        var parts = kana.Split('ー');
        var result = new StringBuilder(ToStringUtil.GetRomanization(parts[0]));
        foreach (var part in parts.Skip(1))
        {
            // Kuromoji omits the katakana long vowel mark. Keep it in the reading.
            if (result.Length > 0)
                result[^1] = result[^1] switch { 'a' => 'ā', 'e' => 'ē', 'i' => 'ī', 'o' => 'ō', 'u' => 'ū', var last => last };
            ToStringUtil.GetRomanization(result, part);
        }
        return result.ToString();
    }

    private static bool ContainsKana(string text) => text.Any(c => c is
        >= '\u3041' and <= '\u3096' or >= '\u30A1' and <= '\u30FA' or >= '\uFF66' and <= '\uFF9D' and not '\uFF70');

    private static bool ContainsIdeographs(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (rune.Value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF
                or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF) return true;
        return false;
    }

    private sealed class SongReadings(LyricsDocument document)
    {
        // Use track context for kanji-only lines when kana occurs in most of the CJK lyrics.
        public bool IsJapanese { get; } = IsJapaneseSong(document.Lines);
        public ConcurrentDictionary<string, string?> Lines { get; } = new(StringComparer.Ordinal);

        private static bool IsJapaneseSong(IReadOnlyList<LyricsLine> lines)
        {
            var kanaLines = lines.Count(line => ContainsKana(line.Text));
            var kanjiOnlyLines = lines.Count(line => !ContainsKana(line.Text) && ContainsIdeographs(line.Text));
            return kanaLines > kanjiOnlyLines;
        }
    }
}

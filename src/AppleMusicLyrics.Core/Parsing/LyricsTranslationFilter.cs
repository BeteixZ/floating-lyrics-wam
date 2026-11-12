using System.Text.RegularExpressions;

namespace AppleMusicLyrics.Core.Parsing;

/// <summary>
/// Removes provider artefacts that occupy translation rows without translating the lyric:
/// placeholder separators such as "//" and credit or copyright lines such as "Lyrics by: X".
/// </summary>
public static class LyricsTranslationFilter
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    // Placeholder rows: only slashes, dashes, dots, middle dots or similar separators.
    private static readonly Regex Placeholder = new(
        @"^[\s/／\\＼|｜\-－—–_.。·・…~～*＊]*$",
        RegexOptions.CultureInvariant, MatchTimeout);

    // "作词：X", "Lyrics by: X", "Composer - X". A separator is required so ordinary lyric
    // text that merely starts with one of these words is never removed.
    private static readonly Regex CreditWithSeparator = new(
        @"^\s*(?:[(\[（【]\s*)?(?:" +
        @"作词|作詞|作曲|编曲|編曲|填词|填詞|词|詞|曲|词曲|詞曲|原唱|演唱|歌手|" +
        @"制作人|製作人|制作|製作|监制|監製|出品|出品人|发行|發行|策划|策劃|统筹|統籌|" +
        @"译者|譯者|翻译|翻譯|译|譯|校对|校對|" +
        @"和声|和聲|和音|配唱|录音|錄音|录音师|錄音師|混音|混音师|混音師|母带|母帶|" +
        @"吉他|贝斯|貝斯|鼓|键盘|鍵盤|弦乐|弦樂|人声|人聲|OP|SP|ISRC|" +
        @"lyrics?|lyricists?|words|written|writers?|songwriters?|music|composed|composers?|composition|" +
        @"arranged|arrangers?|arrangement|produced|producers?|production|executive\s+producers?|" +
        @"translated|translators?|translation|vocals?|backing\s+vocals?|mixed|mixing|mix\s+engineer|" +
        @"mastered|mastering|mastering\s+engineer|recorded|recording|recording\s+engineer|engineers?|" +
        @"publishers?|published|label|guitars?|bass|drums|keyboards?|strings|programming)" +
        @"(?:\s+by)?\s*(?:[)\]）】]\s*)?[:：∶]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

    // "Lyrics by X" without a colon. Restricted to unambiguous credit phrases.
    private static readonly Regex CreditBy = new(
        @"^\s*(?:lyrics|lyricist|words\s+and\s+music|music\s+and\s+lyrics|composed|arranged|produced|" +
        @"translated|written|mixed|mastered|recorded)\s+by\s+\S",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

    // QQ Music appends a translation copyright notice, e.g. "TME享有本翻译作品的著作权".
    private static readonly Regex CopyrightNotice = new(
        @"(?:享有|拥有|擁有)本?(?:翻译|翻譯|歌词|歌詞)?(?:作品)?的?著作权|著作權|未经许可|未經許可|版权所有|版權所有|copyright|©",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>Returns the trimmed translation, or null when it carries no translated lyric.</summary>
    public static string? Clean(string? translation)
    {
        if (string.IsNullOrWhiteSpace(translation))
        {
            return null;
        }

        var trimmed = translation.Trim();
        try
        {
            if (Placeholder.IsMatch(trimmed)
                || CreditWithSeparator.IsMatch(trimmed)
                || CreditBy.IsMatch(trimmed)
                || CopyrightNotice.IsMatch(trimmed))
            {
                return null;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Keep provider text rather than failing a lyric lookup on a pathological line.
        }

        return trimmed;
    }

    public static bool IsMeaningful(string? translation) => Clean(translation) is not null;
}

using System.Text.RegularExpressions;
using AppleMusicLyrics.Core.Matching;

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

    private const string CreditRoles =
        @"作词|作詞|作曲|编曲|編曲|填词|填詞|词|詞|曲|词曲|詞曲|原唱|演唱|歌手|" +
        @"制作人|製作人|制作|製作|助理|监制|監製|出品|出品人|发行|發行|策划|策劃|统筹|統籌|" +
        @"译者|譯者|翻译|翻譯|译|譯|校对|校對|" +
        @"和声|和聲|和音|编写|編寫|配唱|录音|錄音|录音师|錄音師|混音|混音师|混音師|母带|母帶|后期|後期|处理|處理|" +
        @"电吉他|電吉他|吉他|贝斯|貝斯|鼓|键盘|鍵盤|弦乐|弦樂|人声|人聲|钢琴|鋼琴|小提琴|中提琴|大提琴|唢呐|嗩吶|合成|室|" +
        @"歌曲|歌名|专辑|專輯|演唱者|OP|SP|ISRC|" +
        @"lyrics?|lyricists?|words|written|writers?|songwriters?|music|composed|composers?|composition|" +
        @"arranged|arrangers?|arrangement|produced|producers?|production|executive|assistant|additional|track|" +
        @"translated|translators?|translation|vocals?|backing|background|lead|mixed|mixing|mix|" +
        @"mastered|mastering|recorded|recording|engineers?|engineered|engineering|editing|" +
        @"publishers?|published|label|guitars?|bass|drums?|keyboards?|strings?|programming|piano|violin|viola|cello|suona|" +
        @"electric|acoustic|upright|synths?|percussion|tubular|bells|voice|memo|studio|personnel|ar|admin|samples?|interpolates|" +
        @"artists?|singers?|sung|songs?|title|album";

    // Require a credit separator or an explicit "by" phrase, so words such as "Music"
    // and "作词" can still appear in ordinary lyrics. Classify the entire label, including
    // bilingual/compound roles like "编曲 Arrangement" and "Assistant Mixing Engineer".
    private const string CreditPrefix = @"^[\s\p{Cf}(\[（【「『]*";
    private static readonly Regex CreditWithSeparator = new(
        CreditPrefix + @"(?<label>[^:：∶\r\n]{1,160}?)(?<separator>[:：∶]|\s[-－—–]\s)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly Regex CreditLabel = new(
        @"^(?:" + CreditRoles + @")\d*(?:(?:and|与|與|及)?(?:" + CreditRoles + @")\d*)*(?:by|at)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

    // "Lyrics by X" / "Recorded at Studio" without a colon.
    private static readonly Regex CreditBy = new(
        CreditPrefix + @"(?<label>[^:：∶\r\n]{1,160}?)\s+(?:by|at)\s+\S",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly Regex ProviderNotice = new(
        @"^[\s\p{Cf}({（【]*(?:此歌曲为没有填词的纯音乐[，,]?请您欣赏|此歌曲為沒有填詞的純音樂[，,]?請您欣賞|" +
        @"暂不支持歌词|暫不支持歌詞|暂无歌词|暫無歌詞)[。.!！\s)}）】]*$|" +
        @"^[\s\p{Cf}({（【]*more\b.+\blyrics\s+at\s+\S+\.[a-z]{2,}(?:/\S*)?[\s)}）】]*$|" +
        @"^[\s\p{Cf}({（【]*(?:https?://|www\.)\S+[\s)}）】]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

    // QQ Music: "以下歌词翻译由文曲大模型提供"; other sources use similar attribution.
    private static readonly Regex TranslationNotice = new(
        CreditPrefix + @"(?:(?:以下|以上|本首|本)\s*)?(?:(?:歌词|歌詞)\s*)?(?:中文\s*)?" +
        @"(?:翻译|翻譯|译文|譯文)\s*[:：]?\s*(?:由|来自|來自).+(?:提供|生成|制作|製作|完成)" +
        @"|" + CreditPrefix + @"(?:this\s+)?translation\s+(?:(?:is|was)\s+)?(?:provided|generated|powered)\s+by\s+\S",
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

        // Some cached/provider rows contain several physical lines. Remove only the metadata
        // lines rather than dropping a valid translation alongside them.
        if (translation.Contains('\n') || translation.Contains('\r'))
        {
            var rows = translation.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(Clean).Where(row => row is not null);
            var cleaned = string.Join('\n', rows);
            return cleaned.Length == 0 ? null : cleaned;
        }

        var trimmed = translation.Trim();
        if (IsCreditOrNotice(trimmed)) return null;
        try
        {
            if (Placeholder.IsMatch(trimmed))
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

    /// <summary>Identifies provider metadata in either original lyrics or translations.</summary>
    public static bool IsCreditOrNotice(string text)
    {
        if (text.Contains('\n') || text.Contains('\r'))
        {
            var rows = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return rows.Length > 0 && rows.All(IsCreditOrNotice);
        }
        try
        {
            return IsCredit(CreditWithSeparator.Match(text)) || IsCredit(CreditBy.Match(text))
                || TranslationNotice.IsMatch(text) || CopyrightNotice.IsMatch(text) || ProviderNotice.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool IsCredit(Match match)
    {
        if (!match.Success) return false;
        var label = MetadataMatching.Normalize(match.Groups["label"].Value);
        // "Song - Artist" is a title header only with track context, whereas "Song: X" is
        // explicit metadata. Do not mistake a title which happens to be "Music" or "Song".
        if (match.Groups["separator"].Success && match.Groups["separator"].Value.Trim() is "-" or "－" or "—" or "–"
            && label is "song" or "songs" or "title" or "artist" or "artists" or "album" or "music") return false;
        return CreditLabel.IsMatch(label);
    }

    public static bool IsMeaningful(string? translation) => Clean(translation) is not null;
}

using System.Runtime.InteropServices;
using System.Text;

namespace AppleMusicLyrics.App.Presentation;

/// <summary>
/// Maps simplified Chinese characters to the Japanese kanji they stand for, by way of Windows'
/// simplified-to-traditional conversion and the common traditional forms Japan simplified.
/// </summary>
internal static class KanjiVariants
{
    private const uint TraditionalChinese = 0x04000000; // LCMAP_TRADITIONAL_CHINESE

    // Traditional forms that Japanese writes differently (shinjitai). Most traditional forms,
    // such as 離 and 見, are already the Japanese kanji.
    private static readonly Dictionary<char, char> Shinjitai = Pairs(
        "說説 會会 來来 學学 戀恋 變変 國国 歲歳 與与 氣気 聲声 歡歓 舊旧 淚涙 樂楽 體体 實実 點点 擊撃 戰戦 " +
        "黑黒 圖図 單単 邊辺 藝芸 廣広 傳伝 轉転 對対 發発 濕湿 經経 續続 歸帰 滿満 雙双 從従 戲戯 寫写 畫画 " +
        "讀読 賣売 處処 驗験 險険 壞壊 懷懐 靜静 應応 隨随 觸触 獨独 絲糸 繪絵 顏顔 齒歯 鐵鉄 錢銭 爭争 淺浅 " +
        "殘残 稱称 碎砕 醉酔 雜雑 惱悩 腦脳 惡悪 兒児 屬属 擇択 澤沢 譯訳 驛駅 釋釈 燈灯 營営 榮栄 螢蛍 勞労 " +
        "覺覚 擧挙 譽誉 鬪闘 拜拝 亞亜 惠恵 錄録 綠緑 晝昼 盡尽 圓円 價価 假仮 冰氷 瀨瀬 步歩 涉渉 每毎 溫温 " +
        "髮髪 拔抜 佛仏 廳庁 聽聴 德徳 纖繊 關関 擔担 膽胆 鄉郷 禮礼 權権 勸勧 觀観 歷歴 曆暦 戶戸 淨浄 爐炉 " +
        "嶽岳 寶宝 當当 黨党 壓圧 圍囲 隱隠 緣縁 鹽塩 奧奥 櫻桜 樣様 狀状 將将 獎奨 乘乗 剩剰 區区 驅駆 醫医 " +
        "壹壱 數数 團団 斷断 齡齢 靈霊 兩両 晚晩 彈弾 燒焼 搖揺 謠謡 缺欠 據拠 劍剣 儉倹 檢検 顯顕 縣県 效効 " +
        "嚴厳 廢廃 攝摂 狹狭 峽峡 專専 繩縄 萬万");

    private static Dictionary<char, char> Pairs(string pairs)
    {
        var map = new Dictionary<char, char>();
        foreach (var pair in pairs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.Length != 2) throw new InvalidOperationException($"Bad kanji pair '{pair}'.");
            map[pair[0]] = pair[1];
        }
        return map;
    }

    /// <summary>Converts the characters in the given ranges of <paramref name="text"/>.</summary>
    public static string Replace(string text, IReadOnlyList<(int Start, int End)> ranges)
    {
        if (ranges.Count == 0) return text;
        var result = new StringBuilder(text);
        foreach (var (start, end) in ranges)
            for (var i = start; i < end && i < result.Length; i++)
                result[i] = ToKanji(result[i]);
        return result.ToString();
    }

    public static char ToKanji(char value)
    {
        if (char.IsSurrogate(value)) return value;
        var traditional = ToTraditional(value);
        return Shinjitai.TryGetValue(traditional, out var kanji) ? kanji : traditional;
    }

    private static char ToTraditional(char value)
    {
        Span<char> output = stackalloc char[2];
        var input = value.ToString();
        var length = LCMapStringEx("zh-CN", TraditionalChinese, input, 1, ref MemoryMarshal.GetReference(output),
            output.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return length == 1 ? output[0] : value;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int LCMapStringEx(string locale, uint flags, string source, int sourceLength,
        ref char destination, int destinationLength, IntPtr versionInformation, IntPtr reserved, IntPtr sortHandle);
}

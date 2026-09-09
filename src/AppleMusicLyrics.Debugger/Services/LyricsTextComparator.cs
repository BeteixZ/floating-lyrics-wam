using System.Text.RegularExpressions;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Debugger.Models;

namespace AppleMusicLyrics.Debugger.Services;

public static class LyricsTextComparator
{
    private static readonly char[] WordSeparators = { ' ', '\t', '\r', '\n', ',', '.', '!', '?', ';', ':', '"', '\'', '(', ')', '[', ']', '—', '-', '–', '/', '\\', '、', '。', '！', '？', '，' };

    public static (VerdictStatus Verdict, string Reason, double Similarity) Evaluate(
        LyricsDocument? softwareDoc,
        PlayerState player,
        GroundTruthResult groundTruth)
    {
        if (softwareDoc == null || softwareDoc.Lines.Count == 0)
        {
            if (groundTruth.HasLyrics)
            {
                return (
                    VerdictStatus.Missed,
                    $"全网基准源 ({groundTruth.Source}) 存在歌词，但软件未能识别出歌词或被低置信度拦截。",
                    0.0);
            }

            return (
                VerdictStatus.Instrumental,
                "本地缓存与全网权威歌词库均无歌词记录（确认为正常纯音乐/伴奏/无词曲目）。",
                0.0);
        }

        // Software produced a lyric document!
        var softwareLines = softwareDoc.Lines
            .Select(l => l.Text.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        var softwareTokens = ExtractTokens(softwareLines);
        var titleTokens = ExtractTokens(new[] { player.Title ?? "" });
        var titleMatchCount = titleTokens.Count > 0
            ? titleTokens.Count(t => softwareTokens.Contains(t))
            : 0;
        var hasTitleInLyrics = titleTokens.Count > 0 && ((double)titleMatchCount / titleTokens.Count >= 0.5);

        if (groundTruth.HasLyrics && groundTruth.Lines.Count > 0)
        {
            var groundTruthTokens = ExtractTokens(groundTruth.Lines);
            var similarity = CalculateJaccardSimilarity(softwareTokens, groundTruthTokens);

            // Also check line-level direct match
            var matchingLinesCount = 0;
            foreach (var sLine in softwareLines.Take(25))
            {
                var sNorm = NormalizeLine(sLine);
                if (sNorm.Length < 3) continue;
                if (groundTruth.Lines.Any(gLine => NormalizeLine(gLine).Contains(sNorm) || sNorm.Contains(NormalizeLine(gLine))))
                {
                    matchingLinesCount++;
                }
            }

            if (similarity >= 0.18 || matchingLinesCount >= 3)
            {
                return (
                    VerdictStatus.Pass,
                    $"歌词与在线源 ({groundTruth.Source}) 高度吻合 (词汇相似度: {similarity:P0}, 匹配行数: {matchingLinesCount})。",
                    similarity);
            }

            // Both have rich lyrics, but text has almost ZERO overlap (< 10%)
            if (softwareLines.Count >= 5 && groundTruth.Lines.Count >= 5 && similarity < 0.10 && matchingLinesCount <= 1)
            {
                if (hasTitleInLyrics)
                {
                    return (
                        VerdictStatus.PassUnverified,
                        $"歌词词汇与在线源有差异 (可能为不同母带/翻唱/翻译)，但歌词正文中明确唱出了歌名，判定为通过。",
                        similarity);
                }

                return (
                    VerdictStatus.Misidentified,
                    $"【严重错判】软件匹配的歌词与在线基准源 ({groundTruth.Source}) 相似度仅 {similarity:P0} 且歌词中未见歌名，极大概率张冠李戴！",
                    similarity);
            }

            // Moderate overlap or shorter lyric
            return (
                VerdictStatus.Pass,
                $"歌词匹配通过 (参考源: {groundTruth.Source}, 词汇相似度: {similarity:P0})。",
                similarity);
        }

        // Online ground truth had no lyrics
        if (hasTitleInLyrics)
        {
            return (
                VerdictStatus.PassUnverified,
                "在线源暂无参考歌词，但歌词中明确唱出了曲目标题，判定为正确识别。",
                1.0);
        }

        var docDuration = softwareDoc.DurationSeconds ?? 0.0;
        var durationDelta = Math.Abs(docDuration - player.Duration);
        if (durationDelta <= 2.5)
        {
            return (
                VerdictStatus.PassUnverified,
                $"在线源暂无参考歌词，但本地歌词时长与曲目严格吻合 (时长差: {durationDelta:F2}s)，暂定为正确。",
                0.8);
        }

        return (
            VerdictStatus.Misidentified,
            $"【疑似错判】在线源无此歌词，且本地候选时长偏差较明显 ({durationDelta:F1}s)，无法证明内容一致性。",
            0.0);
    }

    private static HashSet<string> ExtractTokens(IEnumerable<string> lines)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var words = line.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words)
            {
                var trimmed = word.Trim().ToLowerInvariant();
                if (trimmed.Length >= 2)
                {
                    tokens.Add(trimmed);
                }
            }
        }
        return tokens;
    }

    private static double CalculateJaccardSimilarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0.0;
        var intersection = a.Count(item => b.Contains(item));
        var union = a.Count + b.Count - intersection;
        return union > 0 ? (double)intersection / union : 0.0;
    }

    private static string NormalizeLine(string s)
    {
        return Regex.Replace(s.ToLowerInvariant(), @"[\s\p{P}]", "");
    }
}

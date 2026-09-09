using System.IO;
using System.Text;
using AppleMusicLyrics.Debugger.Models;

namespace AppleMusicLyrics.Debugger.Services;

public sealed class DiagnosticReportLogger
{
    private readonly string _logFilePath;
    private readonly string _markdownReportPath;
    private readonly object _syncLock = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private readonly List<TrackDiagnosticRecord> _records = new();
    private readonly MonitorSummaryStatistics _stats = new();

    public DiagnosticReportLogger(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss");
        _logFilePath = Path.Combine(outputDirectory, $"overnight_debug_{timestamp}.log");
        _markdownReportPath = Path.Combine(outputDirectory, "overnight_report_latest.md");

        LogInfo($"[INIT] 歌词识别率与防错判调试监控器已启动。");
        LogInfo($"[INIT] 详细日志输出至: {_logFilePath}");
        LogInfo($"[INIT] 晨间报告输出至: {_markdownReportPath}");
    }

    public string LogFilePath => _logFilePath;
    public string MarkdownReportPath => _markdownReportPath;
    public MonitorSummaryStatistics CurrentStats => _stats;
    public IReadOnlyList<TrackDiagnosticRecord> Records => _records;

    public void LogTrack(TrackDiagnosticRecord record)
    {
        lock (_syncLock)
        {
            _records.Add(record);
            UpdateStatistics(record);
            WriteConsoleSummary(record);
            AppendToTextLog(record);
            WriteMarkdownReport();
        }
    }

    public void LogInfo(string message)
    {
        lock (_syncLock)
        {
            var line = $"[{DateTimeOffset.Now:HH:mm:ss}] {message}";
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(line);
            Console.ResetColor();

            try
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }

    private void UpdateStatistics(TrackDiagnosticRecord record)
    {
        _stats.TotalTracksPlayed = _records.Count;

        if (record.Verdict == VerdictStatus.Instrumental)
        {
            _stats.TotalInstrumental++;
        }
        else if (record.Verdict is not (VerdictStatus.Inconclusive or VerdictStatus.TimedOut))
        {
            _stats.TotalWithLyrics++;
        }

        switch (record.Verdict)
        {
            case VerdictStatus.Pass:
                _stats.PassCount++;
                break;
            case VerdictStatus.PassUnverified:
                _stats.PassUnverifiedCount++;
                break;
            case VerdictStatus.Missed:
                _stats.MissedCount++;
                break;
            case VerdictStatus.Misidentified:
                _stats.MisidentifiedCount++;
                break;
            case VerdictStatus.Inconclusive:
            case VerdictStatus.TimedOut:
                _stats.InconclusiveCount++;
                break;
        }

        var successful = record.Verdict is VerdictStatus.Pass or VerdictStatus.PassUnverified;
        if (successful &&
            (record.SoftwareSource.Contains("ttml", StringComparison.OrdinalIgnoreCase) ||
             record.SoftwareSource.Contains("cache", StringComparison.OrdinalIgnoreCase) ||
             record.SoftwareSource.Contains("Local", StringComparison.OrdinalIgnoreCase)))
        {
            _stats.LocalCacheHits++;
        }
        else if (successful && record.SoftwareSource.Contains("LRCLIB", StringComparison.OrdinalIgnoreCase))
        {
            _stats.ExternalLrcHits++;
        }
    }

    private void WriteConsoleSummary(TrackDiagnosticRecord record)
    {
        var durStr = TimeSpan.FromSeconds(record.Duration).ToString(@"mm\:ss");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[TRACK #{_records.Count}] {record.Timestamp:HH:mm:ss} 🎵 正在播放: \"{record.Title}\" — {record.Artist} ({durStr})");
        Console.ResetColor();

        Console.Write($"  ├─ 软件识别: ");
        if (record.SoftwareHasLyrics)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"已出词 ({record.SoftwareSource}, 置信度: {record.SoftwareConfidence})");
            if (record.SoftwareLyricSample.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  │  样例: \"{record.SoftwareLyricSample[0]}\"");
            }
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"未出词 ({record.ResolutionStatus}: {record.ResolutionReason})");
        }
        Console.ResetColor();

        Console.Write($"  ├─ 网络基准: ");
        if (record.GroundTruthHasLyrics)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"命中 {record.GroundTruthSource}");
            if (record.GroundTruthLyricSample.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  │  样例: \"{record.GroundTruthLyricSample[0]}\"");
            }
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"无在线词源 (全网库暂无记录)");
        }
        Console.ResetColor();

        Console.Write($"  └─ 结果判定: ");
        switch (record.Verdict)
        {
            case VerdictStatus.Pass:
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[✅ 正确识别] {record.VerdictReason}");
                break;
            case VerdictStatus.PassUnverified:
                Console.ForegroundColor = ConsoleColor.DarkGreen;
                Console.WriteLine($"[✅ 识别采纳(免校验)] {record.VerdictReason}");
                break;
            case VerdictStatus.Missed:
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[⚠️ 漏识别] {record.VerdictReason}");
                break;
            case VerdictStatus.Misidentified:
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[🚨 严重错判!] {record.VerdictReason}");
                break;
            case VerdictStatus.Instrumental:
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine($"[⚪ 纯音乐曲目] {record.VerdictReason}");
                break;
            case VerdictStatus.Inconclusive:
            case VerdictStatus.TimedOut:
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[⏳ 解析超时/未决] {record.VerdictReason}");
                break;
        }
        Console.ResetColor();

        // Banner
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  -------------------------------------------------------------------------");
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"  [累计统计] 播放: {_stats.TotalTracksPlayed} 首 | 识别率: {_stats.RecognitionRate:F1}% | 错判数: {_stats.MisidentifiedCount} | 漏识别: {_stats.MissedCount} | 耗时: {record.Elapsed.TotalSeconds:F2}s");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  -------------------------------------------------------------------------");
        Console.ResetColor();
    }

    private void AppendToTextLog(TrackDiagnosticRecord record)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"================================================================================");
            sb.AppendLine($"Timestamp: {record.Timestamp:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"Track: \"{record.Title}\" by \"{record.Artist}\" (Album: \"{record.Album}\", Duration: {record.Duration:F2}s)");
            sb.AppendLine($"Verdict: {record.Verdict} ({record.VerdictReason})");
            sb.AppendLine($"Software: HasLyrics={record.SoftwareHasLyrics}, Source={record.SoftwareSource}, Confidence={record.SoftwareConfidence}");
            sb.AppendLine($"Resolution: {record.ResolutionStatus} - {record.ResolutionReason}");
            sb.AppendLine($"Selected/Top Candidate: Score={record.TopCandidateScore}, Delta={record.TopCandidateDurationDelta:F2}s, TotalCandidates={record.CandidateCount}");
            sb.AppendLine($"GroundTruth: HasLyrics={record.GroundTruthHasLyrics}, Source={record.GroundTruthSource}");
            sb.AppendLine($"Similarity: {record.SimilarityScore:P2}");
            sb.AppendLine($"Diagnostic Elapsed: {record.Elapsed.TotalMilliseconds:F0}ms");
            if (record.SoftwareLyricSample.Count > 0)
            {
                sb.AppendLine($"Software Lyrics Sample: {string.Join(" | ", record.SoftwareLyricSample.Take(3))}");
            }
            if (record.GroundTruthLyricSample.Count > 0)
            {
                sb.AppendLine($"Ground Truth Sample: {string.Join(" | ", record.GroundTruthLyricSample.Take(3))}");
            }
            sb.AppendLine();
            File.AppendAllText(_logFilePath, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    public void WriteMarkdownReport()
    {
        try
        {
            var elapsed = DateTimeOffset.Now - _startedAt;
            var elapsedStr = $"{(int)elapsed.TotalHours} 小时 {elapsed.Minutes} 分钟 {elapsed.Seconds} 秒";

            var sb = new StringBuilder();
            sb.AppendLine("# Apple Music 歌词识别率与防错判整夜监控报告");
            sb.AppendLine();
            sb.AppendLine($"- **启动时间**：{_startedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"- **更新时间**：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"- **运行累计耗时**：{elapsedStr}");
            sb.AppendLine($"- **当前软件状态**：后台正常追踪中");
            sb.AppendLine();

            sb.AppendLine("## 1. 核心指标看板 (KPI Dashboard)");
            sb.AppendLine();
            sb.AppendLine("| 指标项 | 数值 | 评估说明 |");
            sb.AppendLine("| :--- | :--- | :--- |");
            sb.AppendLine($"| **累计检测曲目** | **{_stats.TotalTracksPlayed}** 首 | 整夜连续播放的总曲目量 |");
            sb.AppendLine($"| **原曲有词曲目** | **{_stats.TotalWithLyrics}** 首 | 扣除纯音乐/伴奏后的有效歌词曲目 |");
            sb.AppendLine($"| **纯音乐/无词曲目** | **{_stats.TotalInstrumental}** 首 | 权威歌词库确认为无词的曲目（不计入未识别） |");
            sb.AppendLine($"| 🎯 **真实歌词识别率** | **{_stats.RecognitionRate:F1}%** | (成功识别 / 原曲有词曲目) × 100% |");
            sb.AppendLine($"| 🛡️ **张冠李戴错判率** | **{_stats.MisidentificationRate:F1}%** | 错判误匹配歌曲数占比 |");
            sb.AppendLine($"| ✅ **成功识别歌曲数** | **{_stats.PassCount + _stats.PassUnverifiedCount}** 首 | 本地缓存命中: {_stats.LocalCacheHits} / 外部网络: {_stats.ExternalLrcHits} |");
            sb.AppendLine($"| ⚠️ **漏识别曲目数** | **{_stats.MissedCount}** 首 | 网络有词但软件未能出词 |");
            sb.AppendLine($"| 🚨 **严重错判曲目数** | **{_stats.MisidentifiedCount}** 首 | 匹配出非该歌曲歌词的严重问题数 |");
            sb.AppendLine($"| ⏳ **超时未决曲目数** | **{_stats.InconclusiveCount}** 首 | 处于解析中超时，未判定为漏识别 |");
            sb.AppendLine();

            // Section: Misidentifications (Critical!)
            var misidentifiedList = _records.Where(r => r.Verdict == VerdictStatus.Misidentified).ToList();
            sb.AppendLine("## 2. 错判排查 (Misidentified Tracks)");
            sb.AppendLine();
            if (misidentifiedList.Count == 0)
            {
                sb.AppendLine("> [!NOTE]");
                sb.AppendLine("> **太棒了！截至目前未发现任何一例错判（0 误匹配）。**");
                sb.AppendLine("> 软件没有发生将上一首缓存或无关曲目歌词安插给当前歌曲的现象。");
            }
            else
            {
                sb.AppendLine("> [!WARNING]");
                sb.AppendLine($"> **检测到 {misidentifiedList.Count} 起疑似错判现象！** 请核查以下曲目：");
                sb.AppendLine();
                sb.AppendLine("| 时间 | 曲名 | 歌手 | 软件采用来源 | 置信度 | 错判分析 |");
                sb.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- |");
                foreach (var r in misidentifiedList)
                {
                    sb.AppendLine($"| {r.Timestamp:HH:mm:ss} | {r.Title} | {r.Artist} | {r.SoftwareSource} | {r.SoftwareConfidence} | {r.VerdictReason} |");
                }
            }
            sb.AppendLine();

            // Section: Missed Tracks
            var missedList = _records.Where(r => r.Verdict == VerdictStatus.Missed).ToList();
            sb.AppendLine("## 3. 漏识别排查 (Missed Tracks)");
            sb.AppendLine();
            if (missedList.Count == 0)
            {
                sb.AppendLine("> [!NOTE]");
                sb.AppendLine("> 暂无漏识别歌曲，所有具有歌词的曲目均成功出词！");
            }
            else
            {
                sb.AppendLine("> [!IMPORTANT]");
                sb.AppendLine($"> 以下 {missedList.Count} 首歌曲在网络端可查得歌词，但软件未能呈现，原因汇总如下：");
                sb.AppendLine();
                sb.AppendLine("| 时间 | 曲名 | 歌手 | 本地候选情况 | 软件拦截状态 | 网络源 | 归因简析 |");
                sb.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- | :--- |");
                foreach (var r in missedList)
                {
                    sb.AppendLine($"| {r.Timestamp:HH:mm:ss} | {r.Title} | {r.Artist} | {r.CandidateCount}个候选 | {r.ResolutionStatus} | {r.GroundTruthSource} | {r.ResolutionReason} |");
                }
            }
            sb.AppendLine();

            // Section: Full Tracks Table
            sb.AppendLine("## 4. 全量播放追踪流水账 (All Tracks)");
            sb.AppendLine();
            sb.AppendLine("| # | 时间 | 曲名 | 歌手 | 时长 | 判定结果 | 识别来源 | 置信度 | 基准比对源 | 判定详情 |");
            sb.AppendLine("| :-: | :--- | :--- | :--- | :--- | :---: | :--- | :--- | :--- | :--- |");
            var idx = 1;
            foreach (var r in _records)
            {
                var durStr = TimeSpan.FromSeconds(r.Duration).ToString(@"mm\:ss");
                var verdictBadge = r.Verdict switch
                {
                    VerdictStatus.Pass => "✅ 命中",
                    VerdictStatus.PassUnverified => "✅ 采纳",
                    VerdictStatus.Missed => "⚠️ 漏识别",
                    VerdictStatus.Misidentified => "🚨 错判",
                    VerdictStatus.Instrumental => "⚪ 纯音乐",
                    VerdictStatus.Inconclusive => "⏳ 未决",
                    VerdictStatus.TimedOut => "⏳ 超时",
                    _ => r.Verdict.ToString()
                };

                sb.AppendLine($"| {idx++} | {r.Timestamp:HH:mm:ss} | {EscapeMd(r.Title)} | {EscapeMd(r.Artist)} | {durStr} | {verdictBadge} | {r.SoftwareSource} | {r.SoftwareConfidence} | {r.GroundTruthSource} | {EscapeMd(r.VerdictReason)} |");
            }
            sb.AppendLine();

            File.WriteAllText(_markdownReportPath, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    private static string EscapeMd(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "-";
        return s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
    }
}

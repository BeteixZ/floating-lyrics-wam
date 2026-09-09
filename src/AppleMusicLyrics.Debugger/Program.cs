using System.Diagnostics;
using System.IO;
using System.Text;
using AppleMusicLyrics.Core.Configuration;
using AppleMusicLyrics.Debugger.Services;
using AppleMusicLyrics.Infrastructure.Windows.Configuration;

namespace AppleMusicLyrics.Debugger;

public static class Program
{
    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "Apple Music 歌词识别率与防错判调试监控器 (Overnight Monitor)";

        PrintHeader();

        // 1. Check running processes
        CheckRunningProcesses();

        // 2. Load settings
        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AppleMusicLyrics",
            "settings.ini");

        AppSettings settings;
        if (File.Exists(settingsPath))
        {
            var store = new IniSettingsStore(settingsPath);
            settings = store.Load();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[CONFIG] 已载入当前应用配置: {settingsPath}");
        }
        else
        {
            settings = new AppSettings
            {
                CatalogLookupEnabled = true,
                ExternalLyricsEnabled = true
            };
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[CONFIG] 未找到自定义设置，已采用标准生产环境配置。");
        }
        Console.ResetColor();

        // 3. Setup output directory
        var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
        // Also ensure logs directory in project root if running from source
        var projectRootLogs = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "logs"));
        if (Directory.Exists(Path.GetDirectoryName(projectRootLogs)))
        {
            logsDir = projectRootLogs;
        }

        var logger = new DiagnosticReportLogger(logsDir);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\n[EXIT] 收到停止信号 (Ctrl+C)，正在优雅退出并保存晨间汇总报告...");
            Console.ResetColor();
            cts.Cancel();
        };

        using var engine = new OvernightMonitorEngine(settings, logger);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n================================================================================");
        Console.WriteLine(" 监控已就绪！请让 Apple Music 保持放歌。");
        Console.WriteLine(" 调试器将持续记录每首歌的匹配细节与网络基准对比。");
        Console.WriteLine(" 明早只需按下 Ctrl+C，即可查看完整的晨间分析报告！");
        Console.WriteLine("================================================================================\n");
        Console.ResetColor();

        try
        {
            await engine.RunAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal exit
        }

        PrintSummaryBanner(logger);
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
    ╔════════════════════════════════════════════════════════════════════╗
    ║       Apple Music Lyrics - 歌词识别率与防错判整夜调试监控器        ║
    ║       Continuous Recognition & Misidentification Diagnostics       ║
    ╚════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
    }

    private static void CheckRunningProcesses()
    {
        var amRunning = Process.GetProcessesByName("AppleMusic").Length > 0;
        var appRunning = Process.GetProcessesByName("AppleMusicLyrics.App").Length > 0;

        Console.Write("[PROCESS] Apple Music 状态: ");
        if (amRunning)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("正在运行 (PID 已捕获)");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("未检测到运行 (请打开 Apple Music)");
        }
        Console.ResetColor();

        Console.Write("[PROCESS] 悬浮歌词应用状态: ");
        if (appRunning)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("正在运行 (并行观察中)");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("未检测到运行 (调试器将独立使用相同内核进行检测)");
        }
        Console.ResetColor();
    }

    private static void PrintSummaryBanner(DiagnosticReportLogger logger)
    {
        var stats = logger.CurrentStats;
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                        晨间检测数据汇总看板                           ║");
        Console.WriteLine("╠═══════════════════════════════════════════════════════════════════════╣");
        Console.ResetColor();
        Console.WriteLine($"  累计播放歌曲: {stats.TotalTracksPlayed} 首");
        Console.WriteLine($"  原曲有词歌曲: {stats.TotalWithLyrics} 首 (扣除纯音乐/伴奏 {stats.TotalInstrumental} 首)");
        Console.ForegroundColor = stats.RecognitionRate >= 90.0 ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine($"  🎯 真实歌词识别率: {stats.RecognitionRate:F1}% ({stats.PassCount + stats.PassUnverifiedCount}/{stats.TotalWithLyrics})");
        Console.ResetColor();
        Console.ForegroundColor = stats.MisidentifiedCount == 0 ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  🛡️ 错判歌曲数: {stats.MisidentifiedCount} 首 (错判率: {stats.MisidentificationRate:F1}%)");
        Console.ResetColor();
        Console.WriteLine($"  ⚠️ 漏识别歌曲数: {stats.MissedCount} 首");
        Console.WriteLine($"  来源统计: 本地缓存命中 {stats.LocalCacheHits} 首 | 外部网络命中 {stats.ExternalLrcHits} 首");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╠═══════════════════════════════════════════════════════════════════════╣");
        Console.WriteLine($"║ 详细日志文件: {logger.LogFilePath}");
        Console.WriteLine($"║ 晨间报告文件: {logger.MarkdownReportPath}");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
    }
}

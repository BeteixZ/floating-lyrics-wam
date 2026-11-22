using System.Globalization;
using System.Text;

namespace AppleMusicLyrics.Infrastructure.Windows.Diagnostics;

/// <summary>
/// A small local log of errors, so a user can report a crash or lyrics that stopped updating.
/// It never leaves the computer. Repeats of the same error are written once per
/// <see cref="RepeatInterval"/>, and the files are capped at about twice <see cref="MaxFileBytes"/>.
/// Logging never throws.
/// </summary>
public sealed class ErrorLog
{
    public const string FileName = "errors.log";
    public const string PreviousFileName = "errors.1.log";
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, (DateTimeOffset LastWritten, int Suppressed)> _recent = new(StringComparer.Ordinal);

    public ErrorLog(string directory, long maxFileBytes = 512 * 1024, Func<DateTimeOffset>? clock = null)
    {
        Directory = directory;
        MaxFileBytes = Math.Max(1024, maxFileBytes);
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>The app-wide log; set once at startup. Until then entries are dropped.</summary>
    public static ErrorLog? Current { get; set; }

    public string Directory { get; }
    public long MaxFileBytes { get; }
    public string FilePath => Path.Combine(Directory, FileName);

    public static void Report(string area, string message, Exception? error = null) =>
        Current?.Write(area, message, error);

    /// <summary>Writes an unrecoverable error with its full stack trace, ignoring repeats.</summary>
    public static void ReportFatal(string area, Exception error) =>
        Current?.Write(area, "Unhandled exception", error, fatal: true);

    public void Write(string area, string message, Exception? error = null, bool fatal = false)
    {
        try
        {
            lock (_gate)
            {
                var now = _clock();
                var key = $"{area}|{message}|{error?.GetType().FullName}|{error?.Message}";
                var suppressed = 0;
                if (!fatal && _recent.TryGetValue(key, out var recent))
                {
                    if (now - recent.LastWritten < RepeatInterval)
                    {
                        _recent[key] = (recent.LastWritten, recent.Suppressed + 1);
                        return;
                    }
                    suppressed = recent.Suppressed;
                }

                var entry = new StringBuilder()
                    .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                    .Append(" [").Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)).Append(']')
                    .Append(fatal ? " FATAL [" : " ERROR [").Append(area).Append("] ").Append(message);
                if (suppressed > 0) entry.Append(" (repeated ").Append(suppressed).Append(" more times)");
                if (error is not null)
                {
                    entry.Append(": ");
                    // Stack traces only for the unexpected: they make expected failures noisy.
                    entry.Append(fatal ? error.ToString() : $"{error.GetType().Name}: {error.Message}");
                }
                entry.AppendLine();
                if (!Append(entry.ToString())) return;

                // Only a written entry starts the quiet period; a failed write is retried next time.
                if (_recent.Count > 256) _recent.Clear();
                _recent[key] = (now, 0);
            }
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or NotSupportedException or ArgumentException)
        {
        }
    }

    // The update helper and the app it starts can write at the same moment; a named mutex keeps
    // their appends and the rotation from colliding and losing an entry.
    private bool Append(string text)
    {
        using var mutex = new Mutex(false, MutexName);
        var owned = false;
        try
        {
            try
            {
                owned = mutex.WaitOne(TimeSpan.FromSeconds(2));
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }
            if (!owned) return false;

            System.IO.Directory.CreateDirectory(Directory);
            var path = FilePath;
            var info = new FileInfo(path);
            if (info.Exists && info.Length + text.Length > MaxFileBytes)
            {
                File.Move(path, Path.Combine(Directory, PreviousFileName), overwrite: true);
            }
            if (!File.Exists(path))
            {
                File.AppendAllText(path, $"Apple Music Lyrics {AppVersionText} on {Environment.OSVersion}{Environment.NewLine}");
            }
            File.AppendAllText(path, text);
            return true;
        }
        finally
        {
            if (owned) mutex.ReleaseMutex();
        }
    }

    private string MutexName =>
        "AppleMusicLyrics_ErrorLog_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(Directory).ToUpperInvariant())))[..16];

    private static string AppVersionText =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown version";
}

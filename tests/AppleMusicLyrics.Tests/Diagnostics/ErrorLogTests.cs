using AppleMusicLyrics.Infrastructure.Windows.Diagnostics;
using Xunit;

namespace AppleMusicLyrics.Tests.Diagnostics;

public sealed class ErrorLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AppleMusicLyrics.Tests", Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Write_CollapsesRepeatsAndReportsHowManyWereSkipped()
    {
        var log = new ErrorLog(_root, clock: () => _now);
        var error = new TimeoutException("SMTC did not answer");

        log.Write("Media", "Player query timed out", error);
        _now += TimeSpan.FromSeconds(1);
        log.Write("Media", "Player query timed out", error);
        log.Write("Media", "Player query timed out", error);
        log.Write("Lyrics", "Other error");
        _now += ErrorLog.RepeatInterval;
        log.Write("Media", "Player query timed out", error);

        var lines = File.ReadAllLines(log.FilePath);
        Assert.StartsWith("Apple Music Lyrics", lines[0]);
        Assert.Equal(4, lines.Length);
        Assert.Contains(" ERROR [Media] Player query timed out: TimeoutException: SMTC did not answer", lines[1]);
        Assert.Contains("[Lyrics] Other error", lines[2]);
        Assert.Contains("(repeated 2 more times)", lines[3]);
    }

    [Fact]
    public void Write_FatalEntriesKeepTheStackTraceAndAreNeverCollapsed()
    {
        var log = new ErrorLog(_root, clock: () => _now);
        Exception error;
        try { throw new InvalidOperationException("boom"); }
        catch (InvalidOperationException caught) { error = caught; }

        log.Write("App", "Unhandled exception", error, fatal: true);
        log.Write("App", "Unhandled exception", error, fatal: true);

        var text = File.ReadAllText(log.FilePath);
        Assert.Equal(2, text.Split(" FATAL [App]").Length - 1);
        Assert.Contains(nameof(Write_FatalEntriesKeepTheStackTraceAndAreNeverCollapsed), text);
    }

    [Fact]
    public void Write_RotatesToOnePreviousFileSoTheLogStaysSmall()
    {
        var log = new ErrorLog(_root, maxFileBytes: 2048, clock: () => _now);
        for (var index = 0; index < 200; index++)
        {
            log.Write("Area", $"Distinct error {index} {new string('x', 40)}");
        }

        Assert.True(new FileInfo(log.FilePath).Length <= 2048);
        Assert.True(new FileInfo(Path.Combine(_root, ErrorLog.PreviousFileName)).Length <= 2048);
        Assert.Contains("Distinct error 199", File.ReadAllText(log.FilePath));
    }

    [Fact]
    public async Task Write_TwoLogsOnTheSameFileKeepEveryEntry()
    {
        // The update helper and the restarted app log to the same file at the same time.
        var first = new ErrorLog(_root, maxFileBytes: 4096);
        var second = new ErrorLog(_root, maxFileBytes: 4096);

        await Task.WhenAll(
            Task.Run(() => { for (var i = 0; i < 60; i++) first.Write("Helper", $"entry {i}"); }),
            Task.Run(() => { for (var i = 0; i < 60; i++) second.Write("App", $"entry {i}"); }));

        var text = File.ReadAllText(first.FilePath)
            + (File.Exists(Path.Combine(_root, ErrorLog.PreviousFileName)) ? File.ReadAllText(Path.Combine(_root, ErrorLog.PreviousFileName)) : "");
        Assert.Contains("[Helper] entry 59", text);
        Assert.Contains("[App] entry 59", text);
    }

    [Fact]
    public void Write_AFailedWriteIsNotCountedAsWritten()
    {
        Directory.CreateDirectory(_root);
        var log = new ErrorLog(_root, clock: () => _now);
        using (new FileStream(log.FilePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            log.Write("Area", "same error");
        }

        log.Write("Area", "same error");

        Assert.Contains("[Area] same error", File.ReadAllText(log.FilePath));
    }

    [Fact]
    public void Write_NeverThrowsWhenTheDirectoryCannotBeUsed()
    {
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(blocker, "file");
        var log = new ErrorLog(blocker);

        log.Write("Area", "message", new IOException("x"));
        ErrorLog.Report("Area", "no current log is fine");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

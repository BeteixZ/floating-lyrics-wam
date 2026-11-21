using System.Windows;
using System.Diagnostics;
using System.IO;
using AppleMusicLyrics.App.Localization;
using AppleMusicLyrics.App.Services;
using AppleMusicLyrics.Infrastructure.Windows.Updates;

namespace AppleMusicLyrics.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = "AppleMusicLyrics_App_SingleInstance_Mutex";
    private const string UpdateMutexName = "AppleMusicLyrics_App_Update_Mutex";

    // Set in Properties when this launch follows an update, for the main window to report.
    internal const string UpdateOutcomeKey = "UpdateOutcome";
    internal const string UpdateOutcomeInstalled = "installed";
    internal const string UpdateOutcomeFailed = "failed";
    private const string UpdateFailedArgument = "--update-failed";

    protected override void OnStartup(StartupEventArgs e)
    {
        // The update helper (a copy of the previous version): no window, no single-instance lock
        // (the old process still holds it until it exits).
        if (UpdateInstaller.TryParseApplyArguments(e.Args, out var stagingDirectory, out var installDirectory,
                out var oldProcessId))
        {
            using var updateMutex = SingleInstanceGuard.TryAcquire(UpdateMutexName);
            if (updateMutex is not null)
            {
                try { ApplyStagedUpdate(stagingDirectory, installDirectory, oldProcessId); }
                finally { updateMutex.ReleaseMutex(); }
            }
            Shutdown();
            return;
        }

        var updated = e.Args.Contains(UpdateInstaller.UpdatedArgument);
        var updateFailed = e.Args.Contains(UpdateFailedArgument);
        if (updated)
            Current.Properties[UpdateOutcomeKey] = UpdateOutcomeInstalled;
        else if (updateFailed)
            Current.Properties[UpdateOutcomeKey] = UpdateOutcomeFailed;

        using var updateGate = updated || updateFailed ? null : SingleInstanceGuard.TryAcquire(UpdateMutexName);
        if (!updated && !updateFailed && updateGate is null)
        {
            Shutdown();
            return;
        }

        Mutex? mutex;
        try { mutex = SingleInstanceGuard.TryAcquire(MutexName); }
        finally { updateGate?.ReleaseMutex(); }
        if (mutex is null)
        {
            // Another instance is already running
            Shutdown();
            return;
        }

        // Keep mutex alive for the lifetime of the app
        Current.Properties["SingleInstanceMutex"] = mutex;

        StartupUri = new Uri("MainWindow.xaml", UriKind.Relative);
        base.OnStartup(e);
    }

    // Runs from the helper folder. The updated app must remain running through the startup check.
    private static void ApplyStagedUpdate(string stagingDirectory, string installDirectory, int oldProcessId)
    {
        var stagingPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingDirectory));
        var updatesRoot = Path.GetDirectoryName(stagingPath) ?? stagingPath;
        void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(updatesRoot);
                File.AppendAllText(Path.Combine(updatesRoot, "update.log"),
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }

        var executable = Path.Combine(installDirectory, UpdateInstaller.ExecutableName);
        Process StartApp(string argument) => Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = installDirectory,
            ArgumentList = { argument },
        }) ?? throw new InvalidOperationException("No process was started.");

        bool StartUpdatedApp()
        {
            using var process = StartApp(UpdateInstaller.UpdatedArgument);
            return !process.WaitForExit(TimeSpan.FromSeconds(4));
        }

        var logPath = Path.Combine(updatesRoot, "update.log");
        void ReportFailure(string key, string path)
        {
            var text = new LocalizationManager(Path.Combine(installDirectory, "Languages"));
            System.Windows.MessageBox.Show(text.Format(key, path), text.Text("Update.Balloon.Failed.Title"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        try
        {
            if (UpdateInstaller.Apply(stagingDirectory, installDirectory, oldProcessId,
                    TimeSpan.FromSeconds(60), Log, StartUpdatedApp)) return;
        }
        catch (UpdateRollbackException error)
        {
            Log(error.Message);
            ReportFailure("Update.Status.RollbackFailed", error.BackupDirectory);
            return;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log($"Could not apply the update: {error.Message}");
        }

        try
        {
            using var process = StartApp(UpdateFailedArgument);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log($"Could not restart the previous app: {error.Message}");
            ReportFailure("Update.Status.RestartFailed", logPath);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Current.Properties["SingleInstanceMutex"] is Mutex mutex)
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }

        base.OnExit(e);
    }
}

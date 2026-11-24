using System.Windows;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using AppleMusicLyrics.App.Composition;
using AppleMusicLyrics.App.Localization;
using AppleMusicLyrics.App.Services;
using AppleMusicLyrics.Infrastructure.Windows.Diagnostics;
using AppleMusicLyrics.Infrastructure.Windows.Updates;

namespace AppleMusicLyrics.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = "AppleMusicLyrics_App_SingleInstance_Mutex";
    internal const string UpdateMutexName = "AppleMusicLyrics_App_Update_Mutex";
    // Long enough for a starting app to release the update lock it holds briefly.
    private static readonly TimeSpan HelperLockTimeout = TimeSpan.FromSeconds(10);

    // Set in Properties when this launch follows an update, for the main window to report.
    internal const string UpdateOutcomeKey = "UpdateOutcome";
    internal const string UpdateOutcomeInstalled = "installed";
    internal const string UpdateOutcomeFailed = "failed";
    private const string UpdateFailedArgument = "--update-failed";
    // Release CI starts the packaged app with this: it exits with code 0 once the main window
    // has loaded, polling has started and the UI thread has answered, or non-zero on a crash.
    internal const string SmokeTestArgument = "--smoke-test";

    internal static bool IsSmokeTest { get; private set; }

    // After an update, the helper that installed it (also 0.10's) watches the new version for
    // its first four seconds and restores the previous one if it has exited by then. A crash in
    // this window must end the process at once; a later crash is reported like any other.
    internal static readonly TimeSpan UpdatedStartupWindow = TimeSpan.FromSeconds(15);
    private static readonly Stopwatch Uptime = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        Uptime.Start();
        InstallErrorLogging();

        // The update helper (a copy of the previous version): no window, no single-instance lock
        // (the old process still holds it until it exits).
        if (UpdateInstaller.TryParseApplyArguments(e.Args, out var stagingDirectory, out var installDirectory,
                out var oldProcessId, out var readySignal))
        {
            using var updateMutex = SingleInstanceGuard.TryAcquire(UpdateMutexName, HelperLockTimeout);
            if (updateMutex is null)
            {
                // Another update is running. The old app is still waiting for the signal below
                // and keeps running when it does not come.
                ErrorLog.Report("Updates", "Another update holds the update lock; this helper exits");
            }
            else
            {
                try
                {
                    if (!UpdateInstaller.CanApply(stagingDirectory, installDirectory, out var problem))
                    {
                        // Nothing to install: stay silent so the old app keeps running.
                        ErrorLog.Report("Updates", problem);
                    }
                    // Tell the old app it may exit now. An app from 0.10 or earlier passes no
                    // signal and exits without waiting. If the waiting app is gone, nobody
                    // agreed to this installation any more.
                    else if (readySignal is not null && !UpdateHandoff.SignalReady(readySignal))
                    {
                        ErrorLog.Report("Updates", "The app that started this update is no longer waiting; not installing");
                    }
                    else
                    {
                        ApplyStagedUpdate(stagingDirectory, installDirectory, oldProcessId);
                    }
                }
                finally { updateMutex.ReleaseMutex(); }
            }
            Shutdown();
            return;
        }

        IsSmokeTest = e.Args.Contains(SmokeTestArgument);
        var updated = e.Args.Contains(UpdateInstaller.UpdatedArgument);
        var updateFailed = e.Args.Contains(UpdateFailedArgument);
        if (updated)
            Current.Properties[UpdateOutcomeKey] = UpdateOutcomeInstalled;
        else if (updateFailed)
            Current.Properties[UpdateOutcomeKey] = UpdateOutcomeFailed;

        using var updateGate = updated || updateFailed ? null : SingleInstanceGuard.TryAcquire(UpdateMutexName);
        if (!updated && !updateFailed && updateGate is null)
        {
            // A smoke test that never started the app must not pass.
            Shutdown(IsSmokeTest ? 4 : 0);
            return;
        }

        Mutex? mutex;
        try { mutex = SingleInstanceGuard.TryAcquire(MutexName); }
        finally { updateGate?.ReleaseMutex(); }
        if (mutex is null)
        {
            // Another instance is already running
            Shutdown(IsSmokeTest ? 4 : 0);
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

    // Registered before anything else runs, so a startup failure is recorded too.
    private void InstallErrorLogging()
    {
        ErrorLog.Current ??= new ErrorLog(Path.Combine(
            Path.GetDirectoryName(AppCompositionRoot.GetWritableDataPath("settings.ini")) ?? AppContext.BaseDirectory,
            "logs"));
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error) ErrorLog.ReportFatal("Process", error);
        };
        // Not fatal on modern .NET, but a faulted fire-and-forget task is otherwise invisible.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Report("Task", "Unobserved task exception", args.Exception.GetBaseException());
            args.SetObserved();
        };
    }

    private int _reportingCrash;

    // The UI thread's state is unknown after an unhandled exception, so the app records it, tells
    // the user where the log is, and closes instead of carrying on half-broken.
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.ReportFatal("UI", e.Exception);
        if (ExitsSilentlyOnCrash(IsSmokeTest, Current?.Properties[UpdateOutcomeKey] as string, Uptime.Elapsed))
        {
            // A freshly installed version that fails while it starts must exit at once, without a
            // message box: the update helper treats a process that is still running after a few
            // seconds as a successful update and deletes the backup it would otherwise restore.
            Environment.Exit(3);
        }
        if (Interlocked.Exchange(ref _reportingCrash, 1) != 0) return;
        try
        {
            var text = LocalizationManager.Instance;
            System.Windows.MessageBox.Show(text.Format("App.Crash.Text", ErrorLog.Current?.FilePath ?? string.Empty),
                text.Text("App.Crash.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception error)
        {
            ErrorLog.Report("UI", "Could not show the crash message", error);
        }
    }

    /// <summary>
    /// True when a crash should end the app without a message: in a smoke test, or while a
    /// version that was just installed by an update is still within its startup check.
    /// </summary>
    internal static bool ExitsSilentlyOnCrash(bool smokeTest, string? updateOutcome, TimeSpan uptime) =>
        smokeTest || (updateOutcome == UpdateOutcomeInstalled && uptime < UpdatedStartupWindow);

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

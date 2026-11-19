using System.Windows;
using System.Diagnostics;
using System.IO;
using AppleMusicLyrics.App.Services;
using AppleMusicLyrics.Infrastructure.Windows.Updates;

namespace AppleMusicLyrics.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = "AppleMusicLyrics_App_SingleInstance_Mutex";

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
            ApplyStagedUpdate(stagingDirectory, installDirectory, oldProcessId);
            Shutdown();
            return;
        }

        if (e.Args.Contains(UpdateInstaller.UpdatedArgument))
            Current.Properties[UpdateOutcomeKey] = UpdateOutcomeInstalled;
        else if (e.Args.Contains(UpdateFailedArgument))
            Current.Properties[UpdateOutcomeKey] = UpdateOutcomeFailed;

        var mutex = SingleInstanceGuard.TryAcquire(MutexName);
        if (mutex is null)
        {
            // Another instance is already running
            Shutdown();
            return;
        }

        // Keep mutex alive for the lifetime of the app
        Current.Properties["SingleInstanceMutex"] = mutex;

        base.OnStartup(e);
    }

    // Runs from the helper folder: copy the staged new version over the install folder, then start it.
    private static void ApplyStagedUpdate(string stagingDirectory, string installDirectory, int oldProcessId)
    {
        var updatesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppleMusicLyrics", "updates");
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

        bool installed;
        try
        {
            installed = UpdateInstaller.Apply(stagingDirectory, installDirectory, oldProcessId,
                TimeSpan.FromSeconds(60), Log);
        }
        catch (Exception error)
        {
            Log($"Update failed: {error.GetType().Name}.");
            installed = false;
        }

        var executable = Path.Combine(installDirectory, UpdateInstaller.ExecutableName);
        if (!File.Exists(executable)) return;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = installDirectory,
                ArgumentList = { installed ? UpdateInstaller.UpdatedArgument : UpdateFailedArgument },
            });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log($"Could not restart the app: {error.GetType().Name}.");
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

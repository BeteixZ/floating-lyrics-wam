using System.Windows;
using System.Diagnostics;
using AppleMusicLyrics.App.Services;

namespace AppleMusicLyrics.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = "AppleMusicLyrics_App_SingleInstance_Mutex";

    protected override void OnStartup(StartupEventArgs e)
    {
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

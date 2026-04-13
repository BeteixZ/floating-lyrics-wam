using System.Windows;
using System.Diagnostics;

namespace AppleMusicLyrics.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = "AppleMusicLyrics_App_SingleInstance_Mutex";

    protected override void OnStartup(StartupEventArgs e)
    {
        var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // Another instance is already running
            mutex.Dispose();
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

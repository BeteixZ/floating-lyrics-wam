namespace AppleMusicLyrics.App.Services;

public static class SingleInstanceGuard
{
    public static Mutex? TryAcquire(string name)
    {
        var mutex = new Mutex(false, name);
        try
        {
            if (mutex.WaitOne(TimeSpan.Zero, exitContext: false))
            {
                return mutex;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous holder crashed without releasing; WaitOne still granted ownership.
            return mutex;
        }

        mutex.Dispose();
        return null;
    }
}

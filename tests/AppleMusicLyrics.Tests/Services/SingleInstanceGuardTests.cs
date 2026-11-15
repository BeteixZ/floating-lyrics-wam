using AppleMusicLyrics.App.Services;
using Xunit;

namespace AppleMusicLyrics.Tests.Services;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void TryAcquire_SecondInstanceIsRejected()
    {
        var name = "AppleMusicLyrics.Tests_" + Guid.NewGuid().ToString("N");
        using var first = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(first);

        // A mutex is recursively acquirable by its owning thread, so the competing
        // instance must be a different thread, as a second process would be.
        Mutex? second = null;
        var other = new Thread(() => second = SingleInstanceGuard.TryAcquire(name));
        other.Start();
        other.Join();
        Assert.Null(second);

        first!.ReleaseMutex();
    }

    [Fact]
    public void TryAcquire_RecoversAbandonedMutex()
    {
        var name = "AppleMusicLyrics.Tests_" + Guid.NewGuid().ToString("N");
        var holder = new Thread(() =>
        {
            var mutex = SingleInstanceGuard.TryAcquire(name);
            Assert.NotNull(mutex);
            // Terminating without releasing leaves the mutex abandoned, as after a crash.
        });
        holder.Start();
        holder.Join();

        using var recovered = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(recovered);
        recovered!.ReleaseMutex();
    }
}

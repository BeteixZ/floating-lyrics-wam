using AppleMusicLyrics.Infrastructure.Windows.Media;
using Xunit;

namespace AppleMusicLyrics.Tests.Media;

public sealed class SessionSamplerTests
{
    private sealed class Manager(string name)
    {
        public string Name { get; } = name;
    }

    private DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadAsync_AStuckReadTimesOutAndTheNextReadRecovers()
    {
        var hang = true;
        var sampler = new SessionSampler<Manager, string?>(
            _ => Task.FromResult(new Manager("one")),
            async (manager, token) =>
            {
                if (hang) await Task.Delay(Timeout.Infinite, token);
                return manager.Name;
            },
            () => _now)
        { ReadTimeout = TimeSpan.FromMilliseconds(50) };

        // Used to wait forever and hold the polling guard, so lyrics never updated again.
        Assert.Null(await sampler.ReadAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));

        hang = false;
        Assert.Equal("one", await sampler.ReadAsync(CancellationToken.None));
        Assert.Equal(1, sampler.ManagerRequests);
    }

    [Fact]
    public async Task ReadAsync_AManagerRequestThatNeverAnswersIsCancelledAndReplaced()
    {
        var firstCancelled = new TaskCompletionSource();
        var requests = 0;
        var sampler = new SessionSampler<Manager, string?>(
            token =>
            {
                if (Interlocked.Increment(ref requests) > 1) return Task.FromResult(new Manager("second"));
                token.Register(() => firstCancelled.TrySetResult());
                return new TaskCompletionSource<Manager>().Task;
            },
            (manager, _) => Task.FromResult<string?>(manager.Name),
            () => _now)
        { ReadTimeout = TimeSpan.FromMilliseconds(30), ManagerRequestTimeout = TimeSpan.FromSeconds(15) };

        Assert.Null(await sampler.ReadAsync(CancellationToken.None));
        _now += TimeSpan.FromSeconds(5);
        Assert.Null(await sampler.ReadAsync(CancellationToken.None)); // Still within its allowance.
        Assert.Equal(1, sampler.ManagerRequests);

        _now += TimeSpan.FromSeconds(15);
        Assert.Equal("second", await sampler.ReadAsync(CancellationToken.None));
        Assert.Equal(2, sampler.ManagerRequests);
        await firstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReadAsync_RepeatedFailuresRequestAFreshManager()
    {
        var managers = 0;
        var sampler = new SessionSampler<Manager, string?>(
            _ => Task.FromResult(new Manager($"manager {Interlocked.Increment(ref managers)}")),
            (manager, _) => manager.Name == "manager 1"
                ? throw new System.Runtime.InteropServices.COMException("RPC server unavailable")
                : Task.FromResult<string?>(manager.Name),
            () => _now)
        { FailuresBeforeReset = 3, MinResetInterval = TimeSpan.FromSeconds(2) };

        Assert.Null(await sampler.ReadAsync(CancellationToken.None));
        Assert.Null(await sampler.ReadAsync(CancellationToken.None));
        Assert.Null(await sampler.ReadAsync(CancellationToken.None));

        _now += TimeSpan.FromSeconds(3);
        Assert.Equal("manager 2", await sampler.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_AFailedManagerRequestIsRetriedAfterAShortWait()
    {
        var requests = 0;
        var sampler = new SessionSampler<Manager, string?>(
            _ => Interlocked.Increment(ref requests) == 1
                ? throw new InvalidOperationException("early boot")
                : Task.FromResult(new Manager("ok")),
            (manager, _) => Task.FromResult<string?>(manager.Name),
            () => _now)
        { MinResetInterval = TimeSpan.FromSeconds(2) };

        Assert.Null(await sampler.ReadAsync(CancellationToken.None));
        Assert.Null(await sampler.ReadAsync(CancellationToken.None)); // Not retried on every poll.
        Assert.Equal(1, sampler.ManagerRequests);

        _now += TimeSpan.FromSeconds(2);
        Assert.Equal("ok", await sampler.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_ABlockingReadThatIgnoresCancellationIsStillBounded()
    {
        using var release = new ManualResetEventSlim();
        var reads = 0;
        var sampler = new SessionSampler<Manager, string?>(
            _ => Task.FromResult(new Manager("one")),
            (manager, _) =>
            {
                // A synchronous WinRT call that blocks and never looks at the token.
                if (Interlocked.Increment(ref reads) == 1) release.Wait(TimeSpan.FromSeconds(30));
                return Task.FromResult<string?>(manager.Name);
            },
            () => _now)
        { ReadTimeout = TimeSpan.FromMilliseconds(80), FailuresBeforeReset = 100 };

        Assert.Null(await sampler.ReadAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        // While that call is stuck, no second call is stacked on top of it: the poll waits for
        // it until its own deadline and then gives up.
        Assert.Null(await sampler.ReadAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref reads));

        release.Set();
        await Task.Delay(100);
        Assert.Equal("one", await sampler.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_ReadsThatNeverReturnAreCappedAcrossReplacedManagers()
    {
        using var release = new ManualResetEventSlim();
        var started = 0;
        var sampler = new SessionSampler<Manager, string?>(
            _ => Task.FromResult(new Manager("blocked")),
            (manager, _) =>
            {
                Interlocked.Increment(ref started);
                release.Wait(TimeSpan.FromSeconds(30));
                return Task.FromResult<string?>(manager.Name);
            },
            () => _now)
        {
            ReadTimeout = TimeSpan.FromMilliseconds(20),
            FailuresBeforeReset = 1,
            MinResetInterval = TimeSpan.FromMilliseconds(1),
            MaxResetInterval = TimeSpan.FromMilliseconds(1),
            MaxUnansweredReads = 3,
        };

        for (var poll = 0; poll < 12; poll++)
        {
            await sampler.ReadAsync(CancellationToken.None);
            _now += TimeSpan.FromSeconds(1);
        }

        Assert.True(sampler.ManagerRequests > 3);
        Assert.Equal(3, Volatile.Read(ref started));
        release.Set();
    }

    [Fact]
    public async Task ReadAsync_ManagerRequestsThatNeverReturnAreCapped()
    {
        using var release = new ManualResetEventSlim();
        var started = 0;
        var sampler = new SessionSampler<Manager, string?>(
            _ =>
            {
                // RequestAsync blocking before it returns a task.
                Interlocked.Increment(ref started);
                release.Wait(TimeSpan.FromSeconds(30));
                return Task.FromResult(new Manager("late"));
            },
            (manager, _) => Task.FromResult<string?>(manager.Name),
            () => _now)
        {
            ReadTimeout = TimeSpan.FromMilliseconds(20),
            ManagerRequestTimeout = TimeSpan.FromSeconds(1),
            MinResetInterval = TimeSpan.FromMilliseconds(1),
            MaxResetInterval = TimeSpan.FromMilliseconds(1),
            MaxUnansweredRequests = 2,
        };

        for (var poll = 0; poll < 10; poll++)
        {
            await sampler.ReadAsync(CancellationToken.None);
            _now += TimeSpan.FromSeconds(2);
        }

        Assert.Equal(2, sampler.ManagerRequests);
        release.Set();
    }

    [Fact]
    public async Task ReadAsync_RecoversWhenOneStuckRequestIgnoresCancellationAndAnotherHonoursIt()
    {
        using var release = new ManualResetEventSlim();
        var requests = 0;
        var maxInFlight = 0;
        var inFlight = 0;
        var sampler = new SessionSampler<Manager, string?>(
            async token =>
            {
                var number = Interlocked.Increment(ref requests);
                var now = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref maxInFlight, now);
                try
                {
                    if (number == 1) release.Wait(TimeSpan.FromSeconds(30)); // A: ignores cancellation.
                    else if (number == 2) await Task.Delay(Timeout.Infinite, token); // B: honours it.
                    return new Manager($"request {number}");
                }
                finally { Interlocked.Decrement(ref inFlight); }
            },
            (manager, _) => Task.FromResult<string?>(manager.Name),
            () => _now)
        {
            ReadTimeout = TimeSpan.FromMilliseconds(20),
            ManagerRequestTimeout = TimeSpan.FromSeconds(1),
            MinResetInterval = TimeSpan.FromMilliseconds(1),
            MaxResetInterval = TimeSpan.FromMilliseconds(1),
            MaxUnansweredRequests = 2,
        };

        string? result = null;
        for (var poll = 0; poll < 20 && result is null; poll++)
        {
            result = await sampler.ReadAsync(CancellationToken.None);
            _now += TimeSpan.FromSeconds(2);
            await Task.Delay(10);
        }

        Assert.Equal("request 3", result);
        Assert.True(Volatile.Read(ref maxInFlight) <= 2, $"{maxInFlight} requests at once");
        release.Set();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value
            && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    [Fact]
    public async Task ReadAsync_ARecoveredManagerIsKept()
    {
        var failing = true;
        var sampler = new SessionSampler<Manager, string?>(
            _ => Task.FromResult(new Manager("one")),
            (manager, _) => failing ? throw new InvalidOperationException("busy") : Task.FromResult<string?>(manager.Name),
            () => _now)
        { FailuresBeforeReset = 3, MinResetInterval = TimeSpan.FromSeconds(10) };

        for (var poll = 0; poll < 3; poll++) await sampler.ReadAsync(CancellationToken.None);
        failing = false;
        Assert.Equal("one", await sampler.ReadAsync(CancellationToken.None)); // Answers during the cool-down.

        _now += TimeSpan.FromSeconds(30);
        Assert.Equal("one", await sampler.ReadAsync(CancellationToken.None));
        Assert.Equal(1, sampler.ManagerRequests);
    }

    [Fact]
    public async Task ReadAsync_AStuckServiceIsReplacedWithBackoffNotOnEveryPoll()
    {
        var sampler = new SessionSampler<Manager, string?>(
            _ => Task.FromResult(new Manager("stuck")),
            async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return null;
            },
            () => _now)
        { ReadTimeout = TimeSpan.FromMilliseconds(20), FailuresBeforeReset = 3, MinResetInterval = TimeSpan.FromSeconds(10) };

        for (var poll = 0; poll < 12; poll++) await sampler.ReadAsync(CancellationToken.None);
        Assert.Equal(1, sampler.ManagerRequests);

        _now += TimeSpan.FromSeconds(11);
        for (var poll = 0; poll < 3; poll++) await sampler.ReadAsync(CancellationToken.None);
        Assert.Equal(2, sampler.ManagerRequests);

        // The next replacement waits twice as long.
        _now += TimeSpan.FromSeconds(11);
        for (var poll = 0; poll < 3; poll++) await sampler.ReadAsync(CancellationToken.None);
        Assert.Equal(2, sampler.ManagerRequests);
        _now += TimeSpan.FromSeconds(10);
        await sampler.ReadAsync(CancellationToken.None);
        Assert.Equal(3, sampler.ManagerRequests);
    }

    [Fact]
    public async Task ReadAsync_CallerCancellationStillPropagates()
    {
        var sampler = new SessionSampler<Manager, string?>(
            _ => Task.FromResult(new Manager("one")),
            async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return null;
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sampler.ReadAsync(cancellation.Token));
    }
}

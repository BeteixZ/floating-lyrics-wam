using AppleMusicLyrics.Infrastructure.Windows.Diagnostics;

namespace AppleMusicLyrics.Infrastructure.Windows.Media;

/// <summary>
/// Reads one value from a long-lived session manager without letting a stuck or broken manager
/// stop the app for good.
/// <list type="bullet">
/// <item>Requesting a manager, cancelling one and reading all run on the thread pool, and the
/// caller waits at most <see cref="ReadTimeout"/>, even for a synchronous call or one that ignores
/// cancellation.</item>
/// <item>Each manager has at most one read outstanding, and at most
/// <see cref="MaxUnansweredReads"/> unanswered reads exist overall, so calls that never return
/// cannot pile up.</item>
/// <item>A manager is replaced when its request fails, when it does not complete within
/// <see cref="ManagerRequestTimeout"/>, or after <see cref="FailuresBeforeReset"/> failed reads in
/// a row. Replacements back off from <see cref="MinResetInterval"/> to
/// <see cref="MaxResetInterval"/> until a read succeeds again.</item>
/// </list>
/// A failed or timed-out read returns the default value; a later read tries again.
/// </summary>
internal sealed class SessionSampler<TManager, TValue>
    where TManager : class
{
    private readonly Func<CancellationToken, Task<TManager>> _requestManager;
    private readonly Func<TManager, CancellationToken, Task<TValue>> _read;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private Generation? _current;
    private int _consecutiveFailures;
    private int _unansweredReads;
    private int _unansweredRequests;
    private DateTimeOffset _nextGenerationAllowedAt = DateTimeOffset.MinValue;
    // Unset until first used: init properties are applied after the constructor runs.
    private TimeSpan? _resetBackoff;

    private sealed class Generation(Task<TManager> manager, CancellationTokenSource cancellation, DateTimeOffset requestedAt)
    {
        public Task<TManager> Manager { get; } = manager;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public DateTimeOffset RequestedAt { get; } = requestedAt;
        public Task? PendingRead { get; set; }
        public bool Broken { get; set; }
        public bool CancelRequested { get; set; }
    }

    public SessionSampler(
        Func<CancellationToken, Task<TManager>> requestManager,
        Func<TManager, CancellationToken, Task<TValue>> read,
        Func<DateTimeOffset>? clock = null)
    {
        _requestManager = requestManager;
        _read = read;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan ManagerRequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public int FailuresBeforeReset { get; init; } = 3;
    public int MaxUnansweredReads { get; init; } = 3;
    public int MaxUnansweredRequests { get; init; } = 2;
    public TimeSpan MinResetInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxResetInterval { get; init; } = TimeSpan.FromMinutes(5);
    public string LogArea { get; init; } = "Media";

    /// <summary>How many manager requests have been started; for tests and diagnostics.</summary>
    public int ManagerRequests { get; private set; }

    public async Task<TValue?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ReadTimeout);
        var generation = GetGeneration();
        try
        {
            var manager = await generation.Manager.WaitAsync(deadline.Token).ConfigureAwait(false);
            // An earlier read that timed out may still be finishing its cancellation; give it
            // until this read's deadline rather than counting this poll as a failure.
            if (generation.PendingRead is { IsCompleted: false } earlier)
            {
                await earlier.ContinueWith(static _ => { }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            Task<TValue> read;
            lock (_gate)
            {
                if (generation.PendingRead is { IsCompleted: false })
                {
                    throw new TimeoutException("An earlier read of this session manager has not returned.");
                }
                if (_unansweredReads >= MaxUnansweredReads)
                {
                    throw new TimeoutException($"{_unansweredReads} earlier session reads have not returned.");
                }
                _unansweredReads++;
                // The read may block synchronously (WinRT calls marshalled to a busy service);
                // run it off the caller's thread, usually the UI thread.
                var token = deadline.Token;
                read = Task.Run(() => _read(manager, token), CancellationToken.None);
                _ = read.ContinueWith(_ =>
                {
                    lock (_gate) _unansweredReads--;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                generation.PendingRead = read;
            }
            var value = await read.WaitAsync(deadline.Token).ConfigureAwait(false);
            RecordSuccess(generation);
            return value;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException error)
        {
            RecordFailure(generation, $"No answer within {ReadTimeout.TotalSeconds:0.#} s", error);
            return default;
        }
        catch (Exception error)
        {
            RecordFailure(generation, "Read failed", error);
            return default;
        }
    }

    private Generation GetGeneration()
    {
        Generation? retired = null;
        Generation? cancelOnly = null;
        Generation current;
        var reportStuck = false;
        lock (_gate)
        {
            var now = _clock();
            var existing = _current;
            var stuck = existing is { Manager.IsCompleted: false } && now - existing.RequestedAt > ManagerRequestTimeout;
            // Requests that never return hold a thread each; stop asking once a few are stuck.
            var replace = existing is null
                || ((existing.Broken || existing.Manager.IsFaulted || existing.Manager.IsCanceled || stuck)
                    && now >= _nextGenerationAllowedAt && _unansweredRequests < MaxUnansweredRequests);
            if (!replace)
            {
                // Even without a free slot, cancel a request that is overdue: one that honours
                // cancellation then frees its slot, so a later request can still succeed.
                if (stuck && !existing!.CancelRequested)
                {
                    existing.CancelRequested = true;
                    cancelOnly = existing;
                }
                current = existing!;
            }
            else
            {

                reportStuck = stuck;
                retired = existing;
                ManagerRequests++;
                _unansweredRequests++;
                var cancellation = new CancellationTokenSource();
                var token = cancellation.Token;
                // Task.Run: RequestAsync may block before it returns a task.
                var task = Task.Run(() => _requestManager(token), CancellationToken.None);
                _ = task.ContinueWith(t =>
                {
                    lock (_gate) _unansweredRequests--;
                    // Observe a late failure of an abandoned request so it is not reported as unobserved.
                    _ = t.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                current = new Generation(task, cancellation, now);
                _current = current;
                // Every replacement waits longer than the last until a read succeeds again.
                var backoff = _resetBackoff ?? MinResetInterval;
                _nextGenerationAllowedAt = now + backoff;
                _resetBackoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxResetInterval.Ticks));
                _consecutiveFailures = 0;
            }
        }
        if (reportStuck) Log("Session manager request did not complete; requesting it again", null);
        if (cancelOnly is not null)
        {
            Log("Session manager request did not complete; cancelling it", null);
            CancelInBackground(cancelOnly);
        }
        Retire(retired);
        return current;
    }

    private int _logging;

    // Writing the log can wait on the disk and on other processes; never on the caller's thread.
    // While one entry is still being written, further ones are dropped instead of queued: during
    // a lasting failure they repeat, and the log collapses repeats anyway.
    private void Log(string message, Exception? error)
    {
        if (Interlocked.Exchange(ref _logging, 1) != 0) return;
        var area = LogArea;
        _ = Task.Run(() =>
        {
            try { ErrorLog.Report(area, message, error); }
            finally { Volatile.Write(ref _logging, 0); }
        });
    }

    // Cancellation callbacks can block too; run them away from the caller and outside the lock.
    private static void CancelInBackground(Generation generation) => _ = Task.Run(() =>
    {
        try { generation.Cancellation.Cancel(); }
        catch (AggregateException) { }
        catch (ObjectDisposedException) { }
    });

    private static void Retire(Generation? generation)
    {
        if (generation is null) return;
        CancelInBackground(generation);
        // Disposed only once its request has finished: a late callback must not see it disposed.
        _ = generation.Manager.ContinueWith(_ => generation.Cancellation.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void RecordSuccess(Generation generation)
    {
        lock (_gate)
        {
            // A late answer from a replaced manager says nothing about the current one.
            if (!ReferenceEquals(generation, _current)) return;
            // A manager that answers again is healthy: forget the failures and the cool-down.
            _consecutiveFailures = 0;
            _resetBackoff = null;
            _nextGenerationAllowedAt = DateTimeOffset.MinValue;
            generation.Broken = false;
        }
    }

    private void RecordFailure(Generation generation, string message, Exception error)
    {
        Log(message, error);
        lock (_gate)
        {
            if (!ReferenceEquals(generation, _current)) return;
            // The manager may have lost its connection to the media service (Explorer or the
            // service restarted); it is replaced once the backoff allows.
            if (++_consecutiveFailures >= FailuresBeforeReset && generation.Manager.IsCompleted)
            {
                generation.Broken = true;
            }
        }
    }
}

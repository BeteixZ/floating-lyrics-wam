namespace AppleMusicLyrics.Core.Sync;

/// <summary>
/// Turns Apple Music's coarse position reports into a smooth playback estimate.
///
/// Apple Music for Windows (measured November 2026) publishes <c>floor(true position)</c> from a
/// free-running timer that fires about every 1.022s, not at the instant a whole second elapses:
/// 87 consecutive reports fit a single real-time timeline inside a 19ms band, and the report
/// skips a value (+2) roughly every 45s as the timer's phase drifts past a full second. The
/// distance between a report and the true position therefore sweeps the whole 0..1s range in a
/// sawtooth. Any average of <c>reported − elapsed</c> (the previous median estimator) lags by about
/// half a second on average and swings by most of a second on that 45s cycle, which no constant
/// lyric offset can correct.
///
/// Each observed report instead bounds the playback timeline exactly. A report of <c>n</c> that was
/// published at elapsed time <c>e</c> means <c>n ≤ true(e) &lt; n + 1</c>, so the constant
/// <c>true − elapsed</c> lies in <c>[n − e, n + 1 − e)</c>. The publish instant is taken from SMTC's
/// <c>LastUpdatedTime</c> when available, otherwise it is only known to lie between the previous
/// and the current poll, which widens the bound by the poll interval. Intersecting the recent
/// bounds narrows to a few milliseconds once the timer's drifting phase has swept the quantum, and
/// the estimate is the centre of that intersection. A bound that contradicts older ones evicts
/// them, so a small seek or a stray report cannot hold the estimate on a stale timeline.
///
/// What no amount of sampling can reveal is how far SMTC's reports lag the audio actually coming out
/// of the speakers. That constant is the user-facing lyrics offset, and it is applied elsewhere.
/// </summary>
public sealed class PlaybackClock
{
    // About two minutes of reports: long enough for a publish phase drifting by as little as 1%
    // per report to sweep a whole quantum. The measured 2.2% drift needs about 45 reports.
    private const int MaxBounds = 128;

    // Before a timed report has been seen the true position is somewhere in [reported, reported + 1),
    // so the expected phase is half a step. It is replaced as soon as real bounds exist.
    private const double InitialPhasePrior = 0.5;

    // A playing report older than this is not a fresh publish; fall back to poll-bounded timing.
    private const double MaxPublishAgeSeconds = 2.0;

    // Allowance for timestamp rounding and audio/system clock drift when intersecting bounds.
    private const double BoundSlackSeconds = 0.004;

    // A step landing further than this from the running estimate cannot be ordinary jitter: it is a
    // seek, a track change, or a stall. Start over rather than averaging it in.
    private const double DiscontinuitySeconds = 1.5;

    // Corrections are eased in rather than applied at once, so the estimate never jumps backwards
    // across a lyric boundary. Still fast enough to absorb a 0.1s correction in under half a second.
    private const double OffsetSlewRatePerSecond = 0.25;

    // Whole-second SMTC reports normally arrive about a second apart. Allow several missed edges,
    // but never let a buffering/abandoned Playing session advance through the rest of a song.
    private const double MaxExtrapolationSeconds = 3.0;

    private readonly TimeProvider _timeProvider;
    private readonly (double Low, double High)[] _bounds = new (double, double)[MaxBounds];
    private long _startTimestamp;
    private int _boundCount;
    private int _boundStart;
    private double _offset;
    private double _targetOffset;
    private double _playbackElapsed;
    private double _previousPollElapsed;
    private double _lastObservedSeconds;
    private double _lastRawPosition;
    private double _lastRawChangeElapsed;
    private bool _rawStalled;
    private bool _playing;
    private bool _initialized;

    public PlaybackClock(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startTimestamp = _timeProvider.GetTimestamp();
    }

    public void Reset()
    {
        _startTimestamp = _timeProvider.GetTimestamp();
        _boundCount = 0;
        _boundStart = 0;
        _offset = 0;
        _targetOffset = 0;
        _playbackElapsed = 0;
        _previousPollElapsed = 0;
        _lastObservedSeconds = 0;
        _lastRawPosition = 0;
        _lastRawChangeElapsed = 0;
        _rawStalled = false;
        _playing = false;
        _initialized = false;
    }

    /// <param name="rawPosition">The position the player last reported.</param>
    /// <param name="playing">Whether the player reports that it is playing.</param>
    /// <param name="rawPositionAgeSeconds">
    /// How long ago the player published <paramref name="rawPosition"/> (SMTC's
    /// <c>LastUpdatedTime</c>), when known. Without it the publish instant is bounded by the polls.
    /// </param>
    public void Update(double rawPosition, bool playing, double? rawPositionAgeSeconds = null)
    {
        var now = CurrentSeconds;
        var wallDelta = Math.Max(0, now - _lastObservedSeconds);

        // Accumulated against the *previous* playing state: this interval has already elapsed.
        // Both polls and visual reads share the same finite projection horizon.
        var remaining = RemainingBudgetSeconds;
        var advancingDelta = _playing ? Math.Min(wallDelta, remaining) : 0;
        var wasStalled = _rawStalled || (_playing && wallDelta >= remaining);
        _playbackElapsed += advancingDelta;
        _lastObservedSeconds = now;
        var age = rawPositionAgeSeconds is double value && value is >= 0 and <= MaxPublishAgeSeconds ? value : (double?)null;

        if (!_initialized)
        {
            Reseed(rawPosition, playing, age);
            _playing = playing;
            _previousPollElapsed = _playbackElapsed;
            _initialized = true;
            return;
        }

        var rawChanged = Math.Abs(rawPosition - _lastRawPosition) > 0.0005;
        if (rawChanged)
        {
            var predicted = _playbackElapsed + _offset;
            if (!_playing || !playing || wasStalled || Math.Abs(rawPosition - predicted) > DiscontinuitySeconds)
            {
                // A paused seek or the first new edge after a stall is authoritative, even if
                // the jump is small. Old bounds no longer describe this playback epoch.
                Reseed(rawPosition, playing, age);
            }
            else
            {
                // The report changed after the previous poll and no later than this one. A
                // publish timestamp narrows that window to an instant, but never leaves it.
                var publishedEarliest = _previousPollElapsed;
                var publishedLatest = _playbackElapsed;
                if (age is double known)
                {
                    publishedEarliest = publishedLatest = Math.Clamp(_playbackElapsed - known,
                        _previousPollElapsed, _playbackElapsed);
                }

                AddBound(rawPosition - publishedLatest, rawPosition + 1 - publishedEarliest);
                _lastRawPosition = rawPosition;
                _lastRawChangeElapsed = _playbackElapsed;
                _rawStalled = false;
            }
        }
        else if (!_playing && playing)
        {
            // Paused wall time must not consume the watchdog. A resume starts a fresh grace period.
            _lastRawChangeElapsed = _playbackElapsed;
            _rawStalled = false;
        }
        else
        {
            _rawStalled = wasStalled;
        }

        // Do not converge phase while paused (including repeated pause polls) or after a stall.
        if (_playing && playing && !_rawStalled)
        {
            SlewOffset(advancingDelta);
        }
        _playing = playing;
        _previousPollElapsed = _playbackElapsed;
    }

    public double GetEstimatedPosition()
    {
        if (!_initialized)
        {
            return 0;
        }

        var elapsed = _playbackElapsed;
        if (_playing)
        {
            elapsed += Math.Min(Math.Max(0, CurrentSeconds - _lastObservedSeconds), RemainingBudgetSeconds);
        }

        return Math.Max(0, elapsed + _offset);
    }

    /// <summary>
    /// How many more seconds may be projected beyond the position returned by
    /// <see cref="GetEstimatedPosition"/> at the current instant without another raw position edge.
    /// A snapshot consumer can capture this budget alongside that position and cap its own frame
    /// projection. Paused, uninitialized and stale clocks have no remaining projection budget.
    /// </summary>
    public double RemainingExtrapolationSeconds => !_initialized || !_playing ? 0
        : Math.Max(0, RemainingBudgetSeconds - Math.Max(0, CurrentSeconds - _lastObservedSeconds));

    private double RemainingBudgetSeconds => Math.Max(0,
        MaxExtrapolationSeconds - (_playbackElapsed - _lastRawChangeElapsed));

    private double CurrentSeconds => _timeProvider.GetElapsedTime(_startTimestamp).TotalSeconds;

    private void Reseed(double rawPosition, bool playing, double? ageSeconds)
    {
        _boundCount = 0;
        _boundStart = 0;
        _lastRawPosition = rawPosition;
        _lastRawChangeElapsed = _playbackElapsed;
        _rawStalled = false;
        if (playing && ageSeconds is double age)
        {
            // A timestamped report bounds the timeline from its very first observation.
            var published = _playbackElapsed - age;
            AddBound(rawPosition - published, rawPosition + 1 - published);
        }
        else
        {
            _targetOffset = rawPosition + (playing ? InitialPhasePrior : 0) - _playbackElapsed;
        }

        // A seek or track change must land immediately; easing in would drag the old song's phase
        // across the boundary.
        _offset = _targetOffset;
    }

    private void AddBound(double low, double high)
    {
        low -= BoundSlackSeconds;
        high += BoundSlackSeconds;

        // Keep the newest bound. Older ones that contradict it describe a timeline playback has
        // since left (a small seek, a stray early report), so they are evicted oldest first.
        while (_boundCount > 0)
        {
            var (intersectionLow, intersectionHigh) = Intersect(low, high);
            if (intersectionLow <= intersectionHigh) break;
            _boundStart = (_boundStart + 1) % MaxBounds;
            _boundCount--;
        }

        if (_boundCount == MaxBounds)
        {
            _boundStart = (_boundStart + 1) % MaxBounds;
            _boundCount--;
        }

        _bounds[(_boundStart + _boundCount) % MaxBounds] = (low, high);
        _boundCount++;
        var (finalLow, finalHigh) = Intersect(double.NegativeInfinity, double.PositiveInfinity);
        _targetOffset = (finalLow + finalHigh) / 2.0;
    }

    private (double Low, double High) Intersect(double low, double high)
    {
        for (var index = 0; index < _boundCount; index++)
        {
            var bound = _bounds[(_boundStart + index) % MaxBounds];
            low = Math.Max(low, bound.Low);
            high = Math.Min(high, bound.High);
        }

        return (low, high);
    }

    private void SlewOffset(double wallDelta)
    {
        var maxStep = OffsetSlewRatePerSecond * wallDelta;
        _offset += Math.Clamp(_targetOffset - _offset, -maxStep, maxStep);
    }
}

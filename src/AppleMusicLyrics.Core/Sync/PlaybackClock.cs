namespace AppleMusicLyrics.Core.Sync;

/// <summary>
/// Turns Apple Music's coarse position reports into a smooth playback estimate.
///
/// The reported position steps by exactly 1.000s, but the instant each step is published jitters by
/// roughly ±150ms while the long-run rate stays exactly real time (measured on Apple Music for
/// Windows: eleven consecutive steps spanning 11000ms, individual gaps ranging 814–1106ms). So the
/// phase — how far past the reported whole second playback really is — is not something to guess at,
/// it is something to measure: every step contributes one sample of
/// <c>reported position − playback time elapsed</c>, and the median of the recent samples is the
/// phase. Single samples scatter with a standard deviation of 0.083s, so a median over ~8 of them
/// pins it to about ±0.03s.
///
/// What no amount of sampling can reveal is how far SMTC's reports lag the audio actually coming out
/// of the speakers. That constant is the user-facing lyrics offset, and it is applied elsewhere.
/// </summary>
public sealed class PlaybackClock
{
    private const int MaxSamples = 12;

    // Before any step has been observed the reported position is the floor of the true one, so the
    // true phase is somewhere in [0, 1) with an expected value of a half step. Measured phase right
    // after a fresh reading came out at 0.43, so this prior starts close and is replaced by real
    // samples within a second or two.
    private const double InitialPhasePrior = 0.5;

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
    private readonly double[] _samples = new double[MaxSamples];
    private long _startTimestamp;
    private int _sampleCount;
    private int _sampleCursor;
    private double _offset;
    private double _targetOffset;
    private double _playbackElapsed;
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
        _sampleCount = 0;
        _sampleCursor = 0;
        _offset = 0;
        _targetOffset = 0;
        _playbackElapsed = 0;
        _lastObservedSeconds = 0;
        _lastRawPosition = 0;
        _lastRawChangeElapsed = 0;
        _rawStalled = false;
        _playing = false;
        _initialized = false;
    }

    public void Update(double rawPosition, bool playing)
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

        if (!_initialized)
        {
            Reseed(rawPosition, playing);
            _playing = playing;
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
                // the jump is small. Old phase samples no longer describe this playback epoch.
                Reseed(rawPosition, playing);
            }
            else
            {
                AddSample(rawPosition - _playbackElapsed);
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

    private void Reseed(double rawPosition, bool playing)
    {
        _sampleCount = 0;
        _sampleCursor = 0;
        _lastRawPosition = rawPosition;
        _lastRawChangeElapsed = _playbackElapsed;
        _rawStalled = false;
        _targetOffset = rawPosition + (playing ? InitialPhasePrior : 0) - _playbackElapsed;

        // A seek or track change must land immediately; easing in would drag the old song's phase
        // across the boundary.
        _offset = _targetOffset;
    }

    private void AddSample(double sample)
    {
        _samples[_sampleCursor] = sample;
        _sampleCursor = (_sampleCursor + 1) % MaxSamples;
        _sampleCount = Math.Min(_sampleCount + 1, MaxSamples);
        _targetOffset = Median();
    }

    private double Median()
    {
        Span<double> ordered = stackalloc double[_sampleCount];
        for (var index = 0; index < _sampleCount; index++)
        {
            ordered[index] = _samples[index];
        }

        ordered.Sort();

        var middle = _sampleCount / 2;
        return _sampleCount % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2.0;
    }

    private void SlewOffset(double wallDelta)
    {
        var maxStep = OffsetSlewRatePerSecond * wallDelta;
        _offset += Math.Clamp(_targetOffset - _offset, -maxStep, maxStep);
    }
}

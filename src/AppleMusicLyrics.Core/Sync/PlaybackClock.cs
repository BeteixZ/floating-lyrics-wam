using System.Diagnostics;

namespace AppleMusicLyrics.Core.Sync;

public sealed class PlaybackClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private double _estimatedPosition;
    private double _lastRawPosition;
    private long _lastUpdateTimestampMs;
    private long _lastRawChangeTimestampMs;
    private bool _playing;
    private bool _initialized;
    private double _quantizationLeadSeconds = 0.32;

    public void Reset()
    {
        _estimatedPosition = 0;
        _lastRawPosition = 0;
        _lastUpdateTimestampMs = _stopwatch.ElapsedMilliseconds;
        _lastRawChangeTimestampMs = _lastUpdateTimestampMs;
        _playing = false;
        _initialized = false;
        _quantizationLeadSeconds = 0.32;
    }

    public void Update(double rawPosition, bool playing)
    {
        var nowMs = _stopwatch.ElapsedMilliseconds;
        AdvanceEstimate(nowMs);

        if (!_initialized)
        {
            _estimatedPosition = rawPosition + (playing ? _quantizationLeadSeconds : 0);
            _lastRawPosition = rawPosition;
            _lastRawChangeTimestampMs = nowMs;
            _lastUpdateTimestampMs = nowMs;
            _playing = playing;
            _initialized = true;
            return;
        }

        var rawDelta = rawPosition - _lastRawPosition;
        var sampleGapSeconds = Math.Max(0, (nowMs - _lastRawChangeTimestampMs) / 1000.0);
        var positionLooksQuantized = Math.Abs(rawPosition - Math.Round(rawPosition)) < 0.02;

        if (Math.Abs(rawDelta) > 0.001)
        {
            if (playing && rawDelta > 0)
            {
                if (positionLooksQuantized)
                {
                    var candidateLead = Math.Clamp(sampleGapSeconds * 0.5, 0.18, 0.55);
                    _quantizationLeadSeconds = Lerp(_quantizationLeadSeconds, candidateLead, 0.38);
                }
                else
                {
                    _quantizationLeadSeconds = Lerp(_quantizationLeadSeconds, 0.08, 0.18);
                }
            }

            var target = rawPosition + (playing ? _quantizationLeadSeconds : 0);
            if (Math.Abs(target - _estimatedPosition) > 1.2 || rawDelta < -0.25)
            {
                _estimatedPosition = target;
            }
            else
            {
                _estimatedPosition = Lerp(_estimatedPosition, target, 0.55);
            }

            _lastRawPosition = rawPosition;
            _lastRawChangeTimestampMs = nowMs;
        }
        else if (_estimatedPosition < rawPosition)
        {
            _estimatedPosition = rawPosition + (playing ? Math.Min(_quantizationLeadSeconds, 0.15) : 0);
        }

        _lastUpdateTimestampMs = nowMs;
        _playing = playing;
    }

    public double GetEstimatedPosition()
    {
        if (!_initialized)
        {
            return 0;
        }

        var nowMs = _stopwatch.ElapsedMilliseconds;
        var elapsedSeconds = _playing
            ? Math.Max(0, (nowMs - _lastUpdateTimestampMs) / 1000.0)
            : 0;
        return _estimatedPosition + elapsedSeconds;
    }

    private void AdvanceEstimate(long nowMs)
    {
        if (_playing)
        {
            _estimatedPosition += Math.Max(0, (nowMs - _lastUpdateTimestampMs) / 1000.0);
        }
    }

    private static double Lerp(double from, double to, double amount)
    {
        return from + ((to - from) * amount);
    }
}

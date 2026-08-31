namespace AppleMusicLyrics.Core.Display;

/// <summary>
/// Measures the cadence of actual visual-frame callbacks over stable one-second windows.
/// A suspended composition target starts a fresh window instead of publishing a misleading low rate.
/// </summary>
public sealed class VisualFrameStatistics
{
    private static readonly TimeSpan PublicationInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SuspensionGap = TimeSpan.FromMilliseconds(500);

    private TimeSpan? _windowStartedAt;
    private TimeSpan? _lastFrameAt;
    private int _intervalCount;

    public double? FramesPerSecond { get; private set; }

    /// <returns><see langword="true"/> only when a new FPS measurement is published.</returns>
    public bool RecordFrame(TimeSpan renderingTime)
    {
        if (_windowStartedAt is null || _lastFrameAt is null)
        {
            StartWindow(renderingTime);
            return false;
        }

        var frameGap = renderingTime - _lastFrameAt.Value;
        if (frameGap <= TimeSpan.Zero || frameGap > SuspensionGap)
        {
            StartWindow(renderingTime);
            return false;
        }

        _lastFrameAt = renderingTime;
        _intervalCount++;

        var elapsed = renderingTime - _windowStartedAt.Value;
        if (elapsed < PublicationInterval)
        {
            return false;
        }

        FramesPerSecond = _intervalCount / elapsed.TotalSeconds;
        StartWindow(renderingTime);
        return true;
    }

    public void Reset()
    {
        _windowStartedAt = null;
        _lastFrameAt = null;
        _intervalCount = 0;
        FramesPerSecond = null;
    }

    private void StartWindow(TimeSpan renderingTime)
    {
        _windowStartedAt = renderingTime;
        _lastFrameAt = renderingTime;
        _intervalCount = 0;
    }
}

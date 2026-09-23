using System.Runtime.InteropServices;

namespace AppleMusicLyrics.Infrastructure.Windows.Interop;

/// <summary>
/// Reports whether the cursor is over a click-through window, which receives no mouse messages of
/// its own. The cursor is sampled on a background timer rather than through a WH_MOUSE_LL hook: a
/// low-level hook routes every mouse move on the desktop through the installing UI thread, so any
/// stall there (layout, GC, a bitmap render) stutters the system cursor, and Windows silently
/// removes hooks that keep timing out.
/// </summary>
public sealed class MouseTracker : IDisposable
{
    public static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromMilliseconds(50);

    private readonly TimeSpan _sampleInterval;
    private CancellationTokenSource? _sampling;
    private nint _hwnd;
    private Rect _bounds;
    private volatile bool _hasBounds;

    /// <summary>Raised on a background thread whenever the hover state changes.</summary>
    public event Action<bool>? MouseOverChanged;

    public MouseTracker(TimeSpan? sampleInterval = null)
    {
        _sampleInterval = sampleInterval is { } interval && interval > TimeSpan.Zero
            ? interval
            : DefaultSampleInterval;
    }

    public void Start(nint hwnd)
    {
        _hwnd = hwnd;
        if (_sampling is not null)
        {
            return;
        }

        _sampling = new CancellationTokenSource();
        _ = SampleAsync(_sampling.Token);
    }

    // Restrict "mouse over" to an explicit screen rect (device pixels) instead of the whole
    // window — used when the HWND is larger than the visible content (e.g. PureMode card).
    public void SetBounds(int left, int top, int right, int bottom)
    {
        _bounds = new Rect { left = left, top = top, right = right, bottom = bottom };
        _hasBounds = true;
    }

    public void ClearBounds()
    {
        _hasBounds = false;
    }

    public void Stop()
    {
        if (_sampling is null)
        {
            return;
        }

        _sampling.Cancel();
        _sampling.Dispose();
        _sampling = null;
    }

    private async Task SampleAsync(CancellationToken cancellationToken)
    {
        var isMouseOver = false;
        using var timer = new PeriodicTimer(_sampleInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!User32.GetCursorPos(out var cursor))
                {
                    continue;
                }

                var isOver = IsPointInWindow(cursor.x, cursor.y);
                if (isOver != isMouseOver)
                {
                    isMouseOver = isOver;
                    MouseOverChanged?.Invoke(isOver);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private bool IsPointInWindow(int x, int y)
    {
        if (_hasBounds)
        {
            var b = _bounds;
            return x >= b.left && x <= b.right && y >= b.top && y <= b.bottom;
        }

        var hwnd = _hwnd;
        if (hwnd == nint.Zero || !User32.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        return x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom;
    }

    public void Dispose()
    {
        Stop();
    }

    private static class User32
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out Point lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(nint hWnd, out Rect lpRect);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int x;
        public int y;
    }
}

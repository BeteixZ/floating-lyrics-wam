using System.Runtime.InteropServices;

namespace AppleMusicLyrics.Infrastructure.Windows.Interop;

public sealed class MouseTracker : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmMousemove = 0x0200;

    private nint _hookId = nint.Zero;
    private readonly User32.HookProc _hookProc;
    private nint _hwnd;
    private bool _isMouseOver;
    private Rect _bounds;
    private volatile bool _hasBounds;

    public event Action<bool>? MouseOverChanged;

    public MouseTracker()
    {
        _hookProc = HookCallback;
    }

    public void Start(nint hwnd)
    {
        _hwnd = hwnd;
        if (_hookId != nint.Zero)
        {
            return;
        }

        _hookId = User32.SetWindowsHookEx(WhMouseLl, _hookProc, nint.Zero, 0);
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
        if (_hookId == nint.Zero)
        {
            return;
        }

        User32.UnhookWindowsHookEx(_hookId);
        _hookId = nint.Zero;
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && (int)wParam == WmMousemove)
        {
            var structure = Marshal.PtrToStructure<Msllhookstruct>(lParam);
            var isOver = IsPointInWindow(structure.pt.x, structure.pt.y);

            if (isOver != _isMouseOver)
            {
                _isMouseOver = isOver;
                MouseOverChanged?.Invoke(isOver);
            }
        }

        return User32.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private bool IsPointInWindow(int x, int y)
    {
        if (_hasBounds)
        {
            var b = _bounds;
            return x >= b.left && x <= b.right && y >= b.top && y <= b.bottom;
        }

        if (_hwnd == nint.Zero)
        {
            return false;
        }

        _ = User32.GetWindowRect(_hwnd, out var rect);
        return x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom;
    }

    public void Dispose()
    {
        Stop();
    }

    private static class User32
    {
        public delegate nint HookProc(int nCode, nint wParam, nint lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(nint hhk);

        [DllImport("user32.dll")]
        public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

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
    private struct Msllhookstruct
    {
        public Point pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int x;
        public int y;
    }
}

using System.Runtime.InteropServices;

namespace AppleMusicLyrics.Infrastructure.Windows.Interop;

public sealed class WindowInteropService
{
    private const int GwlExStyle = -20;
    private const nint WsExTransparent = 0x00000020;
    private const int WmSyscommand = 0x0112;
    private const nint ScSize = 0xF000;

    public void SetClickThrough(nint hwnd, bool enabled)
    {
        if (hwnd == nint.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(hwnd, GwlExStyle);
        style = enabled
            ? style | WsExTransparent
            : style & ~WsExTransparent;

        _ = SetWindowLongPtr(hwnd, GwlExStyle, style);
    }

    public void BeginResize(nint hwnd, ResizeDirection direction)
    {
        if (hwnd == nint.Zero)
        {
            return;
        }

        _ = ReleaseCapture();
        _ = SendMessage(hwnd, WmSyscommand, ScSize + (nint)direction, nint.Zero);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);
}

public enum ResizeDirection
{
    Left = 1,
    Right = 2,
    Top = 3,
    TopLeft = 4,
    TopRight = 5,
    Bottom = 6,
    BottomLeft = 7,
    BottomRight = 8,
}

using System.Runtime.InteropServices;
using AppleMusicLyrics.Core.Display;

namespace AppleMusicLyrics.Infrastructure.Windows.Display;

public sealed class MonitorService
{
    private const uint MonitorInfoPrimary = 0x00000001;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int MdtEffectiveDpi = 0;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public IReadOnlyList<MonitorDescriptor> GetMonitors()
    {
        var monitors = new List<MonitorDescriptor>();
        MonitorEnumProc callback = (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx
            {
                Size = Marshal.SizeOf<MonitorInfoEx>(),
                DeviceName = string.Empty,
            };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return true;
            }

            var dpiX = 96u;
            var dpiY = 96u;
            _ = GetDpiForMonitor(monitor, MdtEffectiveDpi, out dpiX, out dpiY);
            monitors.Add(new MonitorDescriptor(
                info.DeviceName,
                ToPixelRect(info.Monitor),
                ToPixelRect(info.WorkArea),
                dpiX == 0 ? 96u : dpiX,
                dpiY == 0 ? 96u : dpiY,
                (info.Flags & MonitorInfoPrimary) != 0));
            return true;
        };

        _ = EnumDisplayMonitors(nint.Zero, nint.Zero, callback, nint.Zero);
        return monitors;
    }

    public PixelRect GetWindowRect(nint hwnd)
    {
        return hwnd != nint.Zero && NativeGetWindowRect(hwnd, out var rect)
            ? ToPixelRect(rect)
            : default;
    }

    public bool TryReadSuggestedRect(nint lParam, out PixelRect rect)
    {
        if (lParam == nint.Zero)
        {
            rect = default;
            return false;
        }

        var nativeRect = Marshal.PtrToStructure<NativeRect>(lParam);
        return WindowMessageGeometry.TryCreateSuggestedRect(
            nativeRect.Left,
            nativeRect.Top,
            nativeRect.Right,
            nativeRect.Bottom,
            out rect);
    }

    public MonitorDescriptor? GetMonitorForWindow(nint hwnd, IReadOnlyList<MonitorDescriptor>? monitors = null)
    {
        if (hwnd == nint.Zero)
        {
            return null;
        }

        var monitorHandle = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitorHandle == nint.Zero)
        {
            return null;
        }

        var info = new MonitorInfoEx
        {
            Size = Marshal.SizeOf<MonitorInfoEx>(),
            DeviceName = string.Empty,
        };
        if (!GetMonitorInfo(monitorHandle, ref info))
        {
            return null;
        }

        var available = monitors ?? GetMonitors();
        return available.FirstOrDefault(monitor => string.Equals(
            monitor.DeviceName,
            info.DeviceName,
            StringComparison.OrdinalIgnoreCase));
    }

    public void SetWindowRect(nint hwnd, PixelRect rect)
    {
        if (hwnd == nint.Zero || rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        _ = SetWindowPos(
            hwnd,
            nint.Zero,
            rect.Left,
            rect.Top,
            rect.Width,
            rect.Height,
            SwpNoZOrder | SwpNoActivate);
    }

    private static PixelRect ToPixelRect(NativeRect rect)
    {
        return new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clipRect, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeGetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}

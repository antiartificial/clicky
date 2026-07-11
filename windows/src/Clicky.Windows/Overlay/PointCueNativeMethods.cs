using System.ComponentModel;
using System.Runtime.InteropServices;
using Clicky.Windows.Pointing;

namespace Clicky.Windows.Overlay;

internal interface IPointCueMonitorProvider
{
    PointCueMonitorMetrics GetMonitorContaining(DesktopPoint point);
}

internal sealed class PointCueMonitorProvider : IPointCueMonitorProvider
{
    public PointCueMonitorMetrics GetMonitorContaining(DesktopPoint point)
    {
        var nativePoint = new NativeMethods.Point(
            ToNativeCoordinate(point.X),
            ToNativeCoordinate(point.Y));
        var monitorHandle = NativeMethods.MonitorFromPoint(
            nativePoint,
            NativeMethods.MonitorDefaultToNearest);
        if (monitorHandle == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to locate the target monitor.");
        }

        var monitorInfo = NativeMethods.MonitorInfo.Create();
        if (!NativeMethods.GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read the target monitor bounds.");
        }

        var dpiX = PointCueLayoutCalculator.DefaultDpi;
        var dpiY = PointCueLayoutCalculator.DefaultDpi;
        if (NativeMethods.GetDpiForMonitor(
                monitorHandle,
                NativeMethods.MonitorDpiType.Effective,
                out var nativeDpiX,
                out var nativeDpiY) == 0)
        {
            dpiX = nativeDpiX;
            dpiY = nativeDpiY;
        }

        return new PointCueMonitorMetrics(
            monitorInfo.WorkArea.Left,
            monitorInfo.WorkArea.Top,
            checked(monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left),
            checked(monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top),
            dpiX,
            dpiY);
    }

    private static int ToNativeCoordinate(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Desktop coordinates must be finite.");
        }

        return (int)Math.Clamp(
            Math.Round(value, MidpointRounding.AwayFromZero),
            int.MinValue,
            int.MaxValue);
    }
}

internal static class NativeMethods
{
    internal const int ExtendedWindowStyleIndex = -20;
    internal const long ExtendedStyleNoActivate = 0x08000000L;
    internal const long ExtendedStyleTransparent = 0x00000020L;
    internal const long ExtendedStyleToolWindow = 0x00000080L;

    internal const int WindowMessageMouseActivate = 0x0021;
    internal const int WindowMessageNonClientHitTest = 0x0084;
    internal const int HitTestTransparent = -1;
    internal const int MouseActivateNoActivate = 3;

    internal const uint MonitorDefaultToNearest = 0x00000002;
    internal const uint SetWindowPositionNoActivate = 0x0010;
    internal const uint SetWindowPositionShowWindow = 0x0040;

    internal static readonly nint HwndTopmost = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitorHandle, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(
        nint monitorHandle,
        MonitorDpiType dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(nint windowHandle, int index, int newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint windowHandle, int index, nint newValue);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    internal static nint GetWindowLongPtr(nint windowHandle, int index)
    {
        return nint.Size == 8
            ? GetWindowLongPtr64(windowHandle, index)
            : new nint(GetWindowLong32(windowHandle, index));
    }

    internal static nint SetWindowLongPtr(nint windowHandle, int index, nint newValue)
    {
        return nint.Size == 8
            ? SetWindowLongPtr64(windowHandle, index, newValue)
            : new nint(SetWindowLong32(windowHandle, index, newValue.ToInt32()));
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Point(int x, int y)
    {
        internal readonly int X = x;
        internal readonly int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rectangle
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        internal uint Size;
        internal Rectangle MonitorArea;
        internal Rectangle WorkArea;
        internal uint Flags;

        internal static MonitorInfo Create()
        {
            return new MonitorInfo
            {
                Size = (uint)Marshal.SizeOf<MonitorInfo>()
            };
        }
    }

    internal enum MonitorDpiType
    {
        Effective = 0
    }
}

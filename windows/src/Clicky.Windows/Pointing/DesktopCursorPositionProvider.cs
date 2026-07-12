using System.Runtime.InteropServices;

namespace Clicky.Windows.Pointing;

internal static class DesktopCursorPositionProvider
{
    internal static DesktopPoint? TryGetCurrentPosition()
    {
        return NativeMethods.GetCursorPos(out var point)
            ? new DesktopPoint(point.X, point.Y)
            : null;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out NativePoint point);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}

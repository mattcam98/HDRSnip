using System.Drawing;
using System.Runtime.InteropServices;

namespace HDRSnip.Interop;

/// <summary>
/// Every P/Invoke the app needs, in one place. Nothing else in the codebase
/// declares <c>DllImport</c>.
/// </summary>
internal static partial class Native
{
    // ------------------------------------------------------------ structures

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    // ------------------------------------------------------------ user32

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromPoint(POINT point, uint flags);

    [LibraryImport("user32.dll")]
    internal static partial uint MapVirtualKeyW(uint code, uint mapType);

    // ------------------------------------------------------------ shcore

    [LibraryImport("Shcore.dll")]
    private static partial int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    // ------------------------------------------------------------ dwmapi

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmFlush();

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, in int value, int size);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int CornerPreferenceRound = 2;

    // ------------------------------------------------------------ helpers

    internal static Point GetCursorPosition() =>
        GetCursorPos(out var p) ? new Point(p.X, p.Y) : Point.Empty;

    /// <summary>Effective scale factor (1.0 = 96 DPI) for the monitor under a physical point.</summary>
    internal static double GetMonitorScale(int physicalX, int physicalY)
    {
        const uint monitorDefaultToNearest = 2;
        var monitor = MonitorFromPoint(new POINT { X = physicalX, Y = physicalY }, monitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return 1.0;

        const int mdtEffectiveDpi = 0;
        if (GetDpiForMonitor(monitor, mdtEffectiveDpi, out uint dpiX, out _) != 0 || dpiX == 0)
            return 1.0;

        return dpiX / 96.0;
    }

    /// <summary>Applies the Windows 11 dark title bar and rounded corners. No-op on older builds.</summary>
    internal static void ApplyWindowTheme(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero)
            return;

        int useDark = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, in useDark, sizeof(int));

        int corner = CornerPreferenceRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, in corner, sizeof(int));
    }
}

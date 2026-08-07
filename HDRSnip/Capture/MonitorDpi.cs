using System.Drawing;
using System.Runtime.InteropServices;

namespace HDRSnip.Capture;

/// <summary>
/// Converts DXGI/GDI physical-pixel monitor bounds to WPF DIPs (PerMonitorV2).
/// </summary>
public static class MonitorDpi
{
    private const int MdtEffectiveDpi = 0;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>Effective scale factor (1.0 = 96 DPI) for the monitor containing the point.</summary>
    public static double GetScaleAt(int physicalX, int physicalY)
    {
        var mon = MonitorFromPoint(new POINT { X = physicalX, Y = physicalY }, MonitorDefaultToNearest);
        if (mon == IntPtr.Zero)
            return 1.0;
        if (GetDpiForMonitor(mon, MdtEffectiveDpi, out uint dpiX, out _) != 0 || dpiX == 0)
            return 1.0;
        return dpiX / 96.0;
    }

    public static double GetScale(Rectangle physicalBounds) =>
        GetScaleAt(
            physicalBounds.Left + physicalBounds.Width / 2,
            physicalBounds.Top + physicalBounds.Height / 2);

    public static double GetDpi(Rectangle physicalBounds) => GetScale(physicalBounds) * 96.0;

    /// <summary>Physical desktop pixels → WPF device-independent pixels.</summary>
    public static (double Left, double Top, double Width, double Height) PhysicalToDip(Rectangle physicalBounds)
    {
        double scale = GetScale(physicalBounds);
        if (scale < 0.01) scale = 1.0;
        return (
            physicalBounds.Left / scale,
            physicalBounds.Top / scale,
            physicalBounds.Width / scale,
            physicalBounds.Height / scale);
    }
}

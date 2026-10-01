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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public POINT Position;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorInfo(ref CURSORINFO info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetIconInfo(IntPtr icon, out ICONINFO info);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr handle);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetTopWindow(IntPtr parent);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr hwnd, uint command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);

    [LibraryImport("user32.dll")]
    internal static partial uint MapVirtualKeyW(uint code, uint mapType);

    // ------------------------------------------------------------ shcore

    [LibraryImport("Shcore.dll")]
    private static partial int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    // ------------------------------------------------------------ dwmapi

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmFlush();

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static partial int DwmGetWindowRect(IntPtr hwnd, int attribute, out RECT value, int size);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static partial int DwmGetWindowFlag(IntPtr hwnd, int attribute, out int value, int size);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, in int value, int size);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int CornerPreferenceRound = 2;

    // ------------------------------------------------------------ display config

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [LibraryImport("user32.dll")]
    private static unsafe partial int QueryDisplayConfig(
        uint flags, ref uint pathCount, byte* paths, ref uint modeCount, byte* modes, IntPtr topologyId);

    [LibraryImport("user32.dll")]
    private static unsafe partial int DisplayConfigGetDeviceInfo(byte* requestPacket);

    /// <summary>
    /// Reads the "SDR content brightness" Windows applies to one monitor in HDR
    /// mode, in nits, or null if it cannot be determined.
    /// </summary>
    /// <remarks>
    /// The DISPLAYCONFIG structures are walked as raw bytes: only four fields of
    /// the 72-byte path entry are needed, and declaring the full nested layout
    /// would be several times the size of this method.
    /// </remarks>
    internal static unsafe double? GetSdrWhiteNits(string gdiDeviceName)
    {
        const uint onlyActivePaths = 2;
        const int pathSize = 72, modeSize = 64;
        const int sourceAdapter = 0, sourceId = 8, targetAdapter = 20, targetId = 28;
        const uint getSourceName = 1, getSdrWhiteLevel = 11;
        const int headerSize = 20, sourceNameSize = headerSize + 32 * sizeof(char), whiteLevelSize = headerSize + 4;

        if (GetDisplayConfigBufferSizes(onlyActivePaths, out uint pathCount, out uint modeCount) != 0 || pathCount == 0)
            return null;

        var paths = new byte[pathCount * pathSize];
        var modes = new byte[Math.Max(modeCount, 1) * modeSize];
        byte* request = stackalloc byte[sourceNameSize];

        fixed (byte* pathBase = paths, modeBase = modes)
        {
            if (QueryDisplayConfig(onlyActivePaths, ref pathCount, pathBase, ref modeCount, modeBase, IntPtr.Zero) != 0)
                return null;

            for (uint i = 0; i < pathCount; i++)
            {
                byte* path = pathBase + i * pathSize;

                FillHeader(request, getSourceName, sourceNameSize, path + sourceAdapter, *(uint*)(path + sourceId));
                if (DisplayConfigGetDeviceInfo(request) != 0)
                    continue;

                var name = new string((char*)(request + headerSize));
                if (!name.Equals(gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                    continue;

                FillHeader(request, getSdrWhiteLevel, whiteLevelSize, path + targetAdapter, *(uint*)(path + targetId));
                if (DisplayConfigGetDeviceInfo(request) != 0)
                    return null;

                // Reported in thousandths of the 80-nit scRGB reference white.
                uint level = *(uint*)(request + headerSize);
                return level > 0 ? level / 1000.0 * 80.0 : null;
            }
        }

        return null;

        static void FillHeader(byte* packet, uint type, int size, byte* adapterLuid, uint id)
        {
            new Span<byte>(packet, sourceNameSize).Clear();
            *(uint*)packet = type;
            *(uint*)(packet + 4) = (uint)size;
            *(long*)(packet + 8) = *(long*)adapterLuid;
            *(uint*)(packet + 16) = id;
        }
    }

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

    /// <summary>
    /// Visible bounds of every top-level window on the desktop, front to back, in
    /// physical pixels. Uses the DWM frame bounds, which exclude the invisible
    /// resize border that GetWindowRect includes.
    /// </summary>
    internal static List<Rectangle> GetWindowBounds()
    {
        const uint next = 2;
        const int exStyle = -20, extendedFrameBounds = 9, cloaked = 14;
        const long clickThrough = 0x20;

        var bounds = new List<Rectangle>();
        for (var hwnd = GetTopWindow(IntPtr.Zero); hwnd != IntPtr.Zero; hwnd = GetWindow(hwnd, next))
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
                continue;

            // Overlays that pass the mouse through are not something the user can see to click.
            if (((long)GetWindowLongPtrW(hwnd, exStyle) & clickThrough) != 0)
                continue;

            _ = GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == Environment.ProcessId)
                continue;

            // Cloaked: on another virtual desktop, or a suspended Store app.
            if (DwmGetWindowFlag(hwnd, cloaked, out int isCloaked, sizeof(int)) == 0 && isCloaked != 0)
                continue;

            if (DwmGetWindowRect(hwnd, extendedFrameBounds, out var rect, 16) != 0)
                continue;

            var window = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (window.Width > 8 && window.Height > 8)
                bounds.Add(window);
        }

        return bounds;
    }

    /// <summary>The pointer on screen right now with its hotspot, or null while it is hidden.</summary>
    internal static (IntPtr Handle, Point Position, Point Hotspot)? GetVisibleCursor()
    {
        const int showing = 1;
        var info = new CURSORINFO { Size = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.Flags & showing) == 0 || info.Cursor == IntPtr.Zero)
            return null;

        if (!GetIconInfo(info.Cursor, out var icon))
            return null;

        // GetIconInfo hands back copies of the cursor's bitmaps; only the hotspot is wanted.
        if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask);
        if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color);

        return (info.Cursor, new Point(info.Position.X, info.Position.Y), new Point(icon.HotspotX, icon.HotspotY));
    }

    /// <summary>Makes a window ignore the mouse and never become the foreground window.</summary>
    internal static void MakeClickThrough(IntPtr hwnd)
    {
        const int gwlExStyle = -20;
        const long transparent = 0x20, noActivate = 0x08000000, toolWindow = 0x80;
        SetWindowLongPtrW(hwnd, gwlExStyle, (IntPtr)((long)GetWindowLongPtrW(hwnd, gwlExStyle) | transparent | noActivate | toolWindow));
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

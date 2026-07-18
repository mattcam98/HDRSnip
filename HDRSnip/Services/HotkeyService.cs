using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HDRSnip.Services;

public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private readonly Window _window;
    private readonly Dictionary<int, Action> _handlers = new();
    private HwndSource? _hwndSource;
    private int _nextId = 1;
    private bool _disposed;

    public HotkeyService(Window window)
    {
        _window = window;
        var helper = new WindowInteropHelper(window);
        if (helper.Handle == IntPtr.Zero)
            window.SourceInitialized += OnSourceInitialized;
        else
            Attach(helper.Handle);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _window.SourceInitialized -= OnSourceInitialized;
        Attach(new WindowInteropHelper(_window).Handle);
    }

    private void Attach(IntPtr hwnd)
    {
        _hwndSource = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("No HWND source.");
        _hwndSource.AddHook(WndProc);
    }

    public bool IsReady => new WindowInteropHelper(_window).Handle != IntPtr.Zero;

    public int Register(uint modifiers, uint vk, Action handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var helper = new WindowInteropHelper(_window);
        if (helper.Handle == IntPtr.Zero)
            throw new InvalidOperationException("Window handle not ready.");

        int id = _nextId++;
        if (!RegisterHotKey(helper.Handle, id, modifiers, vk))
            throw new InvalidOperationException(
                $"Failed to register hotkey ({HotkeyText.Format(modifiers, vk)}). Another app may own it.");

        _handlers[id] = handler;
        return id;
    }

    public void UnregisterAll()
    {
        var helper = new WindowInteropHelper(_window);
        if (helper.Handle == IntPtr.Zero) return;
        foreach (var id in _handlers.Keys.ToList())
            UnregisterHotKey(helper.Handle, id);
        _handlers.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            int id = wParam.ToInt32();
            if (_handlers.TryGetValue(id, out var action))
            {
                handled = true;
                _window.Dispatcher.BeginInvoke(action);
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
        _hwndSource?.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

public static class HotkeyText
{
    public static string Format(uint modifiers, uint vk)
    {
        var parts = new List<string>();
        if ((modifiers & 1) != 0) parts.Add("Alt");
        if ((modifiers & 2) != 0) parts.Add("Ctrl");
        if ((modifiers & 4) != 0) parts.Add("Shift");
        if ((modifiers & 8) != 0) parts.Add("Win");
        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    private static string KeyName(uint vk) => vk switch
    {
        0x2C => "PrintScreen",
        >= 0x70 and <= 0x7B => $"F{vk - 0x6F}",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        _ => $"0x{vk:X2}"
    };
}

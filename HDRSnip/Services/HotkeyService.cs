using System.Windows;
using System.Windows.Interop;
using HDRSnip.Interop;
using HDRSnip.Models;

namespace HDRSnip.Services;

/// <summary>
/// Registers global hotkeys against a host window's message loop.
/// Registration failures are reported per hotkey rather than as a batch, so one
/// key already owned by another app does not silently disable the others.
/// </summary>
public sealed class HotkeyService(Window host) : IDisposable
{
    private const int WmHotkey = 0x0312;

    private readonly Window _host = host;
    private readonly Dictionary<int, Action> _handlers = [];
    private HwndSource? _source;
    private int _nextId = 1;
    private bool _disposed;

    /// <summary>Hotkeys that could not be claimed, for surfacing in the UI.</summary>
    public IReadOnlyList<Hotkey> Conflicts { get; private set; } = [];

    private readonly List<Hotkey> _conflicts = [];

    public bool TryRegister(Hotkey hotkey, Action handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (hotkey.IsEmpty)
            return false;

        var handle = EnsureAttached();
        int id = _nextId++;

        if (!Native.RegisterHotKey(handle, id, (uint)hotkey.Modifiers, hotkey.VirtualKey))
        {
            _conflicts.Add(hotkey);
            Conflicts = _conflicts.AsReadOnly();
            return false;
        }

        _handlers[id] = handler;
        return true;
    }

    public void UnregisterAll()
    {
        var handle = new WindowInteropHelper(_host).Handle;
        if (handle != IntPtr.Zero)
        {
            foreach (var id in _handlers.Keys)
                Native.UnregisterHotKey(handle, id);
        }

        _handlers.Clear();
        _conflicts.Clear();
        Conflicts = [];
    }

    private IntPtr EnsureAttached()
    {
        var handle = new WindowInteropHelper(_host).Handle;
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Host window handle is not ready.");

        if (_source is null)
        {
            _source = HwndSource.FromHwnd(handle)
                      ?? throw new InvalidOperationException("Host window has no HWND source.");
            _source.AddHook(WndProc);
        }

        return handle;
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && _handlers.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            // Return to the message loop before doing any capture work.
            _host.Dispatcher.BeginInvoke(action);
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}

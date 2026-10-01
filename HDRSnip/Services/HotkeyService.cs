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

    private readonly Dictionary<int, Action> _handlers = [];
    private readonly List<Hotkey> _conflicts = [];
    private HwndSource? _source;
    private int _nextId = 1;

    /// <summary>Hotkeys that could not be claimed, for surfacing in the UI.</summary>
    public IReadOnlyList<Hotkey> Conflicts => _conflicts;

    public bool TryRegister(Hotkey hotkey, Action handler)
    {
        if (hotkey.IsEmpty)
            return false;

        var handle = EnsureAttached();
        int id = _nextId++;

        if (!Native.RegisterHotKey(handle, id, (uint)hotkey.Modifiers, hotkey.VirtualKey))
        {
            _conflicts.Add(hotkey);
            return false;
        }

        _handlers[id] = handler;
        return true;
    }

    public void UnregisterAll()
    {
        if (_source is { IsDisposed: false })
        {
            foreach (var id in _handlers.Keys)
                Native.UnregisterHotKey(_source.Handle, id);
        }

        _handlers.Clear();
        _conflicts.Clear();
    }

    private IntPtr EnsureAttached()
    {
        if (_source is null)
        {
            var handle = new WindowInteropHelper(host).Handle;
            _source = HwndSource.FromHwnd(handle)
                      ?? throw new InvalidOperationException("Host window handle is not ready.");
            _source.AddHook(WndProc);
        }

        return _source.Handle;
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && _handlers.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            // Return to the message loop before doing any capture work.
            host.Dispatcher.BeginInvoke(action);
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}

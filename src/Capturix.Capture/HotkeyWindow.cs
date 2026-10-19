using Capturix.Core.Hotkeys;
using Capturix.Core.Interop;

namespace Capturix.Capture;

/// <summary>Hidden message-only window that owns the global hotkey registrations.</summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 1;

    public HotkeyWindow()
    {
        // HWND_MESSAGE parent: receives WM_HOTKEY without ever being shown.
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
    }

    /// <summary>Registers <paramref name="binding"/>; returns false if another app already owns it.</summary>
    public bool Register(HotkeyBinding binding, Action handler)
    {
        int id = _nextId++;
        if (!Native.RegisterHotKey(Handle, id, binding.Modifiers | Native.MOD_NOREPEAT, (uint)binding.Key))
            return false;
        _handlers[id] = handler;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (int id in _handlers.Keys)
            Native.UnregisterHotKey(Handle, id);
        _handlers.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && _handlers.TryGetValue(m.WParam.ToInt32(), out var handler))
        {
            handler();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }
}

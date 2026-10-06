using System.Windows.Interop;

namespace Aion2Dps.App.Infrastructure;

public sealed record HotkeyBinding(string Id, string Display, ModifierKeys Modifiers, Key Key, string Description)
{
    public bool Registered { get; set; }
}

/// <summary>
/// System-wide hotkeys through RegisterHotKey on a hidden message-only window. Note: while the game window has focus
/// some games swallow them; overlay buttons cover that case.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, (HotkeyBinding Binding, Action Action)> _map = new();
    private int _nextId = 0xA200;

    public GlobalHotkeys()
    {
        var p = new HwndSourceParameters("Aion2Dps hotkeys") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    public IReadOnlyList<HotkeyBinding> Bindings => _map.Values.Select(v => v.Binding).ToList();

    public static IReadOnlyList<HotkeyBinding> Defaults() =>
    [
        new("reset", "Ctrl+Alt+D", ModifierKeys.Control | ModifierKeys.Alt, Key.D, "Reset the meter"),
        new("copy", "Ctrl+Alt+C", ModifierKeys.Control | ModifierKeys.Alt, Key.C, "Copy the damage summary (chat line)"),
        new("overlay", "Ctrl+Alt+O", ModifierKeys.Control | ModifierKeys.Alt, Key.O, "Show / hide the overlay"),
        new("lock", "Ctrl+Alt+L", ModifierKeys.Control | ModifierKeys.Alt, Key.L, "Lock overlay + click-through on/off"),
        new("party", "Ctrl+Alt+P", ModifierKeys.Control | ModifierKeys.Alt, Key.P, "Party members only on/off"),
    ];

    public bool Register(HotkeyBinding binding, Action action)
    {
        int id = _nextId++;
        uint mods = NativeMethods.MOD_NOREPEAT;
        if (binding.Modifiers.HasFlag(ModifierKeys.Control)) mods |= NativeMethods.MOD_CONTROL;
        if (binding.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= NativeMethods.MOD_ALT;
        if (binding.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= NativeMethods.MOD_SHIFT;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(binding.Key);
        binding.Registered = NativeMethods.RegisterHotKey(_source.Handle, id, mods, vk);
        if (!binding.Registered) AppLog.Warn("Hotkeys", $"{binding.Display} is already used by another application");
        _map[id] = (binding, action);
        return binding.Registered;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _map.TryGetValue(wParam.ToInt32(), out var entry))
        {
            handled = true;
            try { entry.Action(); }
            catch (Exception ex) { AppLog.Error("Hotkeys", $"{entry.Binding.Display} failed", ex); }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _map.Keys) NativeMethods.UnregisterHotKey(_source.Handle, id);
        _map.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}

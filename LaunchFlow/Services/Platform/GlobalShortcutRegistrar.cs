using System.Runtime.InteropServices;
using System.Windows.Input;

namespace LaunchFlow.Services;

/// <summary>
/// Parses and registers one global Win32 hotkey. UI capture/prompting stays in
/// MainWindow; this owns the active shortcut string and native register/rollback.
/// </summary>
public sealed class GlobalShortcutRegistrar
{
    public const int DefaultHotkeyId = 0x4C46;

    public string ActiveShortcut { get; private set; } = "";

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public static bool TryParse(string shortcut, out uint flags, out uint key)
    {
        flags = 0x4000; // MOD_NOREPEAT
        key = 0;
        var parts = shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;
        foreach (var part in parts[..^1])
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) flags |= 0x0002;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) flags |= 0x0001;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) flags |= 0x0004;
            else return false;
        }
        if ((flags & 7) == 0) return false;
        if (!Enum.TryParse<Key>(parts[^1], true, out var parsed)) return false;
        if (parsed is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System) return false;
        key = (uint)KeyInterop.VirtualKeyFromKey(parsed);
        return key != 0;
    }

    public bool TrySet(IntPtr hwnd, string shortcut, int hotkeyId = DefaultHotkeyId)
    {
        if (hwnd == IntPtr.Zero || !TryParse(shortcut, out uint modifiers, out uint key)) return false;
        if (ActiveShortcut.Length > 0) UnregisterHotKey(hwnd, hotkeyId);
        if (!RegisterHotKey(hwnd, hotkeyId, modifiers, key))
        {
            if (ActiveShortcut.Length > 0 && TryParse(ActiveShortcut, out var oldMods, out var oldKey))
                RegisterHotKey(hwnd, hotkeyId, oldMods, oldKey);
            return false;
        }
        ActiveShortcut = shortcut;
        return true;
    }

    public void Clear(IntPtr hwnd, int hotkeyId = DefaultHotkeyId)
    {
        if (hwnd != IntPtr.Zero) UnregisterHotKey(hwnd, hotkeyId);
        ActiveShortcut = "";
    }

    public void UnregisterOnly(IntPtr hwnd, int hotkeyId = DefaultHotkeyId)
    {
        if (hwnd != IntPtr.Zero) UnregisterHotKey(hwnd, hotkeyId);
    }
}

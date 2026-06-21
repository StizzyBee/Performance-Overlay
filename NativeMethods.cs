using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PerformanceOverlay;

/// <summary>P/Invoke helpers for click-through overlay behaviour and a global hotkey.</summary>
internal static class NativeMethods
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080; // keep overlay out of Alt-Tab

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    public const int WM_HOTKEY = 0x0312;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    /// <summary>Make the window transparent to mouse input (click-through) or restore it.</summary>
    public static void SetClickThrough(Window window, bool enabled)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;
        if (enabled) ex |= WS_EX_TRANSPARENT;
        else ex &= ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    /// <summary>Tag the window as a tool window so it never shows in the taskbar / Alt-Tab.</summary>
    public static void HideFromAltTab(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_LAYERED);
    }
}

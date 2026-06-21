using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace PerformanceOverlay;

public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            NativeMethods.HideFromAltTab(this);
            RegisterHotkey();
        };
        MouseLeftButtonDown += OnDragStart;
    }

    private bool _locked;

    /// <summary>True while the user is dragging the overlay; the app skips its heavy
    /// per-tick sensor refresh during this so the drag stays smooth.</summary>
    public bool IsDragging { get; private set; }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (_locked) return;
        IsDragging = true;
        try { DragMove(); }
        catch { /* not draggable right now */ }
        finally { IsDragging = false; }

        var s = App.Current.Settings;
        s.PositionX = Left;
        s.PositionY = Top;
        s.Save();
    }

    /// <summary>Apply settings: position, lock/click-through, and the shared overlay visual.</summary>
    public void ApplySettings(AppSettings s)
    {
        Left = s.PositionX;
        Top = s.PositionY;
        _locked = s.LockPosition;
        View.ApplySettings(s);
        NativeMethods.SetClickThrough(this, s.LockPosition && s.ClickThrough);
    }

    public void UpdateReadings(AppSettings s)
    {
        if (IsDragging) return; // keep dragging smooth
        View.UpdateReadings();
    }

    // ---------- global hotkey ----------

    private const int HotkeyId = 0xC0DE;
    private HwndSource? _source;

    private void RegisterHotkey()
    {
        var s = App.Current.Settings;
        var helper = new WindowInteropHelper(this);
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(WndProc);

        uint mods = NativeMethods.MOD_NOREPEAT;
        foreach (var m in s.HotkeyModifiers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            mods |= m.ToLowerInvariant() switch
            {
                "control" or "ctrl" => NativeMethods.MOD_CONTROL,
                "shift" => NativeMethods.MOD_SHIFT,
                "alt" => NativeMethods.MOD_ALT,
                "win" => NativeMethods.MOD_WIN,
                _ => 0u,
            };
        }

        uint vk = 0x4F; // 'O'
        if (Enum.TryParse<Key>(s.HotkeyKey, true, out var key))
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);

        NativeMethods.UnregisterHotKey(helper.Handle, HotkeyId);
        NativeMethods.RegisterHotKey(helper.Handle, HotkeyId, mods, vk);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            App.Current.ToggleOverlay();
            handled = true;
        }
        return IntPtr.Zero;
    }
}

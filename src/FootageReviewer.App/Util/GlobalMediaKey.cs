using System;
using System.Runtime.InteropServices;

namespace FootageReviewer.App.Util;

/// <summary>
/// Listens for the keyboard's media Play/Pause key SYSTEM-WIDE so playback can be toggled while the app is
/// tabbed out. Uses a low-level keyboard hook (WH_KEYBOARD_LL) rather than RegisterHotKey because Avalonia
/// doesn't expose a WndProc to receive WM_HOTKEY. The hook callback runs on the thread that installed it, so
/// install it from the UI thread (which pumps messages).
/// </summary>
public static class GlobalMediaKey
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_MEDIA_PLAY_PAUSE = 0xB3;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Kept in a static field: if this is collected the hook silently stops firing.
    private static HookProc? _proc;
    private static IntPtr _hook = IntPtr.Zero;
    private static Action? _onPlayPause;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    /// <summary>Start listening. <paramref name="onPlayPause"/> is invoked on the hook (UI) thread.</summary>
    public static void Install(Action onPlayPause)
    {
        if (_hook != IntPtr.Zero) return;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        _onPlayPause = onPlayPause;
        _proc = HookCallback; // hold the delegate alive
        try { _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0); }
        catch { _hook = IntPtr.Zero; }
        if (_hook == IntPtr.Zero) DiagnosticsLogger.Log("global media key: hook not installed");
    }

    public static void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        try { UnhookWindowsHookEx(_hook); } catch { /* shutting down */ }
        _hook = IntPtr.Zero;
        _proc = null;
        _onPlayPause = null;
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (data.vkCode == VK_MEDIA_PLAY_PAUSE)
                {
                    _onPlayPause?.Invoke();
                    return 1; // swallow it so a background music player doesn't also toggle
                }
            }
        }
        catch { /* a hook callback must never throw */ }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}

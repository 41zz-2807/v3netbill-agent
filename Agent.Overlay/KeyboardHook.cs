using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace V3Netbill.Agent.Overlay;

/// <summary>
/// Global low-level keyboard hook (WH_KEYBOARD_LL) — ACTIVE ONLY when session is Locked.
/// Blocks: Alt+Tab, Win keys (LWin/RWin), Alt+F4.
/// Does NOT block Ctrl+Alt+Del (Secure Attention Sequence - by design uncatchable).
/// </summary>
public class KeyboardHook : IDisposable
{
    private readonly ILogger<KeyboardHook> _logger;
    private IntPtr _hookId = IntPtr.Zero;
    private readonly HookProc _hookProc;
    private bool _active;

    public KeyboardHook(ILogger<KeyboardHook> logger)
    {
        _logger = logger;
        _hookProc = HookCallback; // keep delegate alive
    }

    public void Enable()
    {
        if (_active) return;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle("user32.dll"), 0);
        if (_hookId == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            _logger.LogError("SetWindowsHookEx failed with error {Error}", error);
            return;
        }
        _active = true;
        _logger.LogInformation("Keyboard hook ENABLED (blocking Alt+Tab, Win, Alt+F4)");
    }

    public void Disable()
    {
        if (!_active) return;
        if (_hookId != IntPtr.Zero)
        {
            bool ok = UnhookWindowsHookEx(_hookId);
            if (!ok)
            {
                int error = Marshal.GetLastWin32Error();
                _logger.LogWarning("UnhookWindowsHookEx failed with error {Error}", error);
            }
            _hookId = IntPtr.Zero;
        }
        _active = false;
        _logger.LogInformation("Keyboard hook DISABLED");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (IsBlockedKey(kb))
            {
                _logger.LogDebug("Blocked key: vkCode={VkCode}, flags={Flags}", kb.vkCode, kb.flags);
                return (IntPtr)1; // block
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static bool IsBlockedKey(KBDLLHOOKSTRUCT kb)
    {
        // VK codes
        const int VK_TAB = 0x09;
        const int VK_LWIN = 0x5B;
        const int VK_RWIN = 0x5C;
        const int VK_F4 = 0x73;
        const int VK_MENU = 0x12; // Alt
        const int VK_CONTROL = 0x11; // Ctrl
        const int VK_ESCAPE = 0x1B;

        // Alt+Tab (Alt is down via SYSKEY, Tab is KEYDOWN)
        if (kb.vkCode == VK_TAB && (GetAsyncKeyState(VK_MENU) & 0x8000) != 0)
            return true;

        // Win keys (LWin/RWin)
        if (kb.vkCode == VK_LWIN || kb.vkCode == VK_RWIN)
            return true;

        // Alt+F4
        if (kb.vkCode == VK_F4 && (GetAsyncKeyState(VK_MENU) & 0x8000) != 0)
            return true;

        // Ctrl+Alt+Del is SAS - CANNOT be intercepted by any hook (by Windows design)
        // We explicitly do NOT attempt to block it.

        return false;
    }

    public void Dispose() => Disable();

    // ===== P/Invoke =====
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
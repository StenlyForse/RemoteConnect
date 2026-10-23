using System.ComponentModel;
using System.Runtime.InteropServices;
using RemoteConnect.Windows.Interop;

namespace RemoteConnect.Windows.Input;

public readonly record struct KeyboardHookEvent(ushort VirtualKey, ushort ScanCode, bool IsDown, bool IsExtended);

/// <summary>
/// Низкоуровневый перехват клавиатуры для окна просмотра: позволяет передать Alt+Tab, Win и т.п. (FR-INP-4).
/// Ставится на UI-потоке. Обработчик должен работать быстро; true — клавиша поглощается локально.
/// Синтетический ввод (в т.ч. от самого приложения) пропускается без обработки.
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    private readonly Func<KeyboardHookEvent, bool> _handler;
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private IntPtr _hook;

    public LowLevelKeyboardHook(Func<KeyboardHookEvent, bool> handler)
    {
        _handler = handler;
        _proc = HookProc;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            var message = (int)wParam;
            var isKeyMessage = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_KEYUP
                or NativeMethods.WM_SYSKEYDOWN or NativeMethods.WM_SYSKEYUP;

            if (isKeyMessage && (data.flags & NativeMethods.LLKHF_INJECTED) == 0)
            {
                var e = new KeyboardHookEvent(
                    (ushort)data.vkCode,
                    (ushort)data.scanCode,
                    IsDown: (data.flags & NativeMethods.LLKHF_UP) == 0,
                    IsExtended: (data.flags & NativeMethods.LLKHF_EXTENDED) != 0);
                bool swallow;
                try
                {
                    swallow = _handler(e);
                }
                catch
                {
                    swallow = false;
                }

                if (swallow)
                {
                    return 1;
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public static IntPtr ForegroundWindow => NativeMethods.GetForegroundWindow();
}

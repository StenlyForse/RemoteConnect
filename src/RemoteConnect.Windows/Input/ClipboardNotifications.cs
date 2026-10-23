using System.ComponentModel;
using System.Runtime.InteropServices;
using RemoteConnect.Windows.Interop;

namespace RemoteConnect.Windows.Input;

/// <summary>Подписка окна на уведомления об изменении буфера обмена (<c>WM_CLIPBOARDUPDATE</c>).</summary>
public static class ClipboardNotifications
{
    public const int WM_CLIPBOARDUPDATE = 0x031D;

    /// <summary>Родитель для невидимого окна, которое только принимает сообщения.</summary>
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    public static void Subscribe(IntPtr hwnd)
    {
        if (!NativeMethods.AddClipboardFormatListener(hwnd))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public static void Unsubscribe(IntPtr hwnd) => NativeMethods.RemoveClipboardFormatListener(hwnd);
}

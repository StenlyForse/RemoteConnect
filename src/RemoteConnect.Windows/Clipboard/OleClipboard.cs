using System.Runtime.InteropServices;
using RemoteConnect.Windows.Interop;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace RemoteConnect.Windows.Clipboard;

/// <summary>Буфер обмена OLE: кладёт COM-объект без немедленной выдачи содержимого. Вызывать на STA-потоке.</summary>
public static class OleClipboard
{
    private const int CLIPBRD_E_CANT_OPEN = unchecked((int)0x800401D0);

    [ThreadStatic]
    private static bool _oleInitialized;

    public static void Set(ComIDataObject dataObject)
    {
        EnsureOleInitialized();
        for (var attempt = 1; ; attempt++)
        {
            var hr = NativeMethods.OleSetClipboard(dataObject);
            if (hr == 0)
            {
                return;
            }

            if (hr != CLIPBRD_E_CANT_OPEN || attempt >= 5)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            Thread.Sleep(30);
        }
    }

    /// <summary>OLE-буфер требует OleInitialize на потоке (в WPF он уже вызван; повторный вызов безопасен).</summary>
    private static void EnsureOleInitialized()
    {
        if (_oleInitialized)
        {
            return;
        }

        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("Буфер обмена OLE доступен только на STA-потоке");
        }

        var hr = NativeMethods.OleInitialize(IntPtr.Zero);
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        _oleInitialized = true;
    }

    public static bool IsCurrent(ComIDataObject dataObject) => NativeMethods.OleIsCurrentClipboard(dataObject) == 0;

    /// <summary>Очищает буфер, если в нём всё ещё наш объект.</summary>
    public static void ClearIfCurrent(ComIDataObject dataObject)
    {
        if (IsCurrent(dataObject))
        {
            NativeMethods.OleSetClipboard(null);
        }
    }
}

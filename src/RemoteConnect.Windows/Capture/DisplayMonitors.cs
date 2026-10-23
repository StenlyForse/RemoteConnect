using System.Runtime.InteropServices;
using RemoteConnect.Windows.Interop;

namespace RemoteConnect.Windows.Capture;

/// <summary>Монитор в физических пикселях виртуального рабочего стола.</summary>
public sealed record DisplayMonitor(string DeviceName, int Left, int Top, int Width, int Height, bool IsPrimary);

public static class DisplayMonitors
{
    /// <summary>Список мониторов; основной — первым.</summary>
    public static IReadOnlyList<DisplayMonitor> GetAll()
    {
        var monitors = new List<DisplayMonitor>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        return monitors.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Left).ThenBy(m => m.Top).ToList();

        bool Callback(IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data)
        {
            var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(), szDevice = string.Empty };
            if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                var r = info.rcMonitor;
                monitors.Add(new DisplayMonitor(
                    info.szDevice.TrimStart('\\', '.'),
                    r.Left,
                    r.Top,
                    r.Right - r.Left,
                    r.Bottom - r.Top,
                    (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0));
            }

            return true;
        }
    }
}

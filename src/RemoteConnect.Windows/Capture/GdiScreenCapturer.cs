using System.Runtime.InteropServices;
using RemoteConnect.Windows.Interop;

namespace RemoteConnect.Windows.Capture;

public sealed class ScreenCaptureException(string message) : Exception(message);

/// <summary>
/// Захват монитора через GDI BitBlt в DIB-секцию (BGRA, строки сверху вниз) с отрисовкой курсора.
/// Захват через Desktop Duplication — следующий шаг оптимизации (см. plan.md, этап 4).
/// </summary>
public sealed class GdiScreenCapturer : IDisposable
{
    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _oldBitmap;
    private IntPtr _bits;
    private int _width;
    private int _height;

    public bool DrawCursor { get; set; } = true;

    public static int StrideFor(int width) => width * 4;

    /// <summary>Снимает монитор в <paramref name="target"/> (размер не меньше Width*Height*4).</summary>
    public void Capture(DisplayMonitor monitor, byte[] target)
    {
        ObjectDisposedException.ThrowIf(_memDc == IntPtr.Zero && _width < 0, this);
        var size = monitor.Width * monitor.Height * 4;
        if (target.Length < size)
        {
            throw new ArgumentException("Буфер меньше кадра", nameof(target));
        }

        EnsureSurface(monitor.Width, monitor.Height);

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new ScreenCaptureException("Нет доступа к экрану");
        }

        try
        {
            if (!NativeMethods.BitBlt(_memDc, 0, 0, monitor.Width, monitor.Height, screenDc, monitor.Left, monitor.Top, NativeMethods.SRCCOPY))
            {
                // Обычно означает защищённый рабочий стол (UAC, экран блокировки).
                throw new ScreenCaptureException("Экран недоступен для захвата");
            }
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }

        if (DrawCursor)
        {
            DrawCursorOnSurface(monitor);
        }

        NativeMethods.GdiFlush();
        Marshal.Copy(_bits, target, 0, size);
    }

    public void Dispose()
    {
        ReleaseSurface();
        _width = -1;
    }

    private void DrawCursorOnSurface(DisplayMonitor monitor)
    {
        var info = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
        if (!NativeMethods.GetCursorInfo(ref info) || (info.flags & NativeMethods.CURSOR_SHOWING) == 0 || info.hCursor == IntPtr.Zero)
        {
            return;
        }

        var hotspotX = 0;
        var hotspotY = 0;
        if (NativeMethods.GetIconInfo(info.hCursor, out var iconInfo))
        {
            hotspotX = iconInfo.xHotspot;
            hotspotY = iconInfo.yHotspot;
            if (iconInfo.hbmMask != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(iconInfo.hbmMask);
            }

            if (iconInfo.hbmColor != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(iconInfo.hbmColor);
            }
        }

        var x = info.ptScreenPos.X - monitor.Left - hotspotX;
        var y = info.ptScreenPos.Y - monitor.Top - hotspotY;
        if (x < -64 || y < -64 || x > monitor.Width || y > monitor.Height)
        {
            return;
        }

        NativeMethods.DrawIconEx(_memDc, x, y, info.hCursor, 0, 0, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);
    }

    private void EnsureSurface(int width, int height)
    {
        if (_memDc != IntPtr.Zero && width == _width && height == _height)
        {
            return;
        }

        ReleaseSurface();

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            _memDc = NativeMethods.CreateCompatibleDC(screenDc);
            var header = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // отрицательная высота — строки сверху вниз
                biPlanes = 1,
                biBitCount = 32,
            };
            _dib = NativeMethods.CreateDIBSection(screenDc, ref header, NativeMethods.DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
            if (_memDc == IntPtr.Zero || _dib == IntPtr.Zero)
            {
                ReleaseSurface();
                throw new ScreenCaptureException("Не удалось создать буфер захвата");
            }

            _oldBitmap = NativeMethods.SelectObject(_memDc, _dib);
            _width = width;
            _height = height;
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void ReleaseSurface()
    {
        if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero)
        {
            NativeMethods.SelectObject(_memDc, _oldBitmap);
        }

        if (_dib != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_dib);
        }

        if (_memDc != IntPtr.Zero)
        {
            NativeMethods.DeleteDC(_memDc);
        }

        _memDc = _dib = _oldBitmap = _bits = IntPtr.Zero;
        _width = _height = 0;
    }
}

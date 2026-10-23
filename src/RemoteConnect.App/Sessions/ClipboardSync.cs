using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteConnect.Core.Protocol;
using RemoteConnect.Core.Session;
using RemoteConnect.Windows.Input;
using Serilog;

namespace RemoteConnect.App.Sessions;

/// <summary>
/// Синхронизация буфера обмена в рамках одной сессии (spec §5.6). Создаётся после «Разрешить»
/// и реагирует только на изменения после этого момента (FR-CLP-3). Работает на UI-потоке.
/// </summary>
public sealed class ClipboardSync : IDisposable
{
    private const int ClipboardRetries = 5;

    private readonly Connection _connection;
    private readonly Dispatcher _dispatcher;
    private HwndSource? _window;

    public ClipboardSync(Connection connection, Dispatcher dispatcher)
    {
        _connection = connection;
        _dispatcher = dispatcher;
    }

    /// <summary>Собеседник включил или выключил синхронизацию.</summary>
    public event Action<bool>? RemoteEnabledChanged;

    public ClipboardSyncPolicy Policy { get; } = new(TimeProvider.System);

    /// <summary>Начинает слушать изменения буфера. Вызывать на UI-потоке.</summary>
    public void Start()
    {
        _window = new HwndSource(new HwndSourceParameters("RemoteConnect.Clipboard")
        {
            ParentWindow = ClipboardNotifications.HWND_MESSAGE,
            WindowStyle = 0,
        });
        _window.AddHook(WndProc);
        ClipboardNotifications.Subscribe(_window.Handle);
    }

    public void SetLocalEnabled(bool enabled)
    {
        Policy.LocalEnabled = enabled;
        _connection.Send(new ClipboardEnabled(enabled));
        Log.Information("Буфер обмена: синхронизация {State} локально", enabled ? "включена" : "выключена");
    }

    /// <summary>Обрабатывает сообщения буфера от собеседника; можно вызывать с любого потока.</summary>
    public bool TryHandle(Message message)
    {
        switch (message)
        {
            case ClipboardEnabled enabled:
                Policy.RemoteEnabled = enabled.Enabled;
                _dispatcher.InvokeAsync(() => RemoteEnabledChanged?.Invoke(enabled.Enabled));
                return true;
            case ClipboardData data:
                _dispatcher.InvokeAsync(() => Apply(data));
                return true;
            default:
                return false;
        }
    }

    public void Dispose()
    {
        if (_window is { } window)
        {
            _window = null;
            ClipboardNotifications.Unsubscribe(window.Handle);
            window.RemoveHook(WndProc);
            window.Dispose();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == ClipboardNotifications.WM_CLIPBOARDUPDATE)
        {
            // Даём приложению-источнику закончить запись в буфер.
            _dispatcher.InvokeAsync(OnLocalChanged, DispatcherPriority.Background);
        }

        return IntPtr.Zero;
    }

    private void OnLocalChanged()
    {
        if (_window is null || !_connection.IsOpen)
        {
            return;
        }

        try
        {
            var content = ReadLocal();
            if (content is not { } c)
            {
                return;
            }

            if (!ClipboardSyncPolicy.WithinLimits(c.Format, c.Size))
            {
                Log.Information("Буфер обмена: {Format} слишком большой ({Size}), не передаётся", c.Format, c.Size);
                return;
            }

            if (Policy.ShouldSend(c.Hash))
            {
                _connection.Send(new ClipboardData(c.Format, c.Data));
                Log.Debug("Буфер обмена: отправлено {Format}, {Bytes} байт", c.Format, c.Data.Length);
            }
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            Log.Debug(ex, "Буфер обмена: не удалось прочитать");
        }
    }

    private void Apply(ClipboardData data)
    {
        // Длина текста в символах проверяется после декодирования.
        if (_window is null || !Policy.ShouldApply(data.Format, data.Format == ClipboardFormat.Image ? data.Data.Length : 0))
        {
            return;
        }

        try
        {
            switch (data.Format)
            {
                case ClipboardFormat.Text:
                    var text = System.Text.Encoding.UTF8.GetString(data.Data);
                    if (text.Length > ClipboardSyncPolicy.MaxTextChars)
                    {
                        return;
                    }

                    Policy.OnApplied(ClipboardSyncPolicy.Hash(ClipboardFormat.Text, data.Data));
                    Retry(() => Clipboard.SetText(text));
                    break;

                case ClipboardFormat.Image:
                    var image = DecodePng(data.Data);
                    Policy.OnApplied(HashImage(image));
                    Retry(() => Clipboard.SetImage(image));
                    break;
            }

            Log.Debug("Буфер обмена: получено {Format}, {Bytes} байт", data.Format, data.Data.Length);
        }
        catch (Exception ex) when (ex is ExternalException or NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            Log.Debug(ex, "Буфер обмена: не удалось записать полученные данные");
        }
    }

    private static Content? ReadLocal()
    {
        if (Retry(() => Clipboard.ContainsText()))
        {
            var text = Retry(() => Clipboard.GetText());
            if (text.Length > ClipboardSyncPolicy.MaxTextChars)
            {
                return new Content(ClipboardFormat.Text, [], [], text.Length);
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            return new Content(ClipboardFormat.Text, bytes, ClipboardSyncPolicy.Hash(ClipboardFormat.Text, bytes), text.Length);
        }

        if (Retry(() => Clipboard.ContainsImage()) && Retry(() => Clipboard.GetImage()) is { } source)
        {
            var image = ToBgr32(source);
            var png = EncodePng(image);
            return new Content(ClipboardFormat.Image, png, HashImage(image), png.Length);
        }

        // Файлы из Проводника и прочие форматы не передаются (FR-CLP-7).
        return null;
    }

    /// <summary>
    /// Пиксели приводятся к Bgr32 до хеширования: так хеш совпадает до и после записи в буфер,
    /// и записанная картинка не уходит обратно «эхом» (FR-CLP-5).
    /// </summary>
    private static BitmapSource ToBgr32(BitmapSource source)
    {
        BitmapSource converted = source.Format == PixelFormats.Bgr32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static byte[] HashImage(BitmapSource image)
    {
        var stride = image.PixelWidth * 4;
        var buffer = new byte[8 + (stride * image.PixelHeight)];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), image.PixelWidth);
        BitConverter.TryWriteBytes(buffer.AsSpan(4, 4), image.PixelHeight);
        image.CopyPixels(new Int32Rect(0, 0, image.PixelWidth, image.PixelHeight), buffer, stride, 8);
        return ClipboardSyncPolicy.Hash(ClipboardFormat.Image, buffer);
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static BitmapSource DecodePng(byte[] png)
    {
        using var ms = new MemoryStream(png);
        var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return ToBgr32(decoder.Frames[0]);
    }

    /// <summary>Буфер может быть кратковременно занят другим приложением (CLIPBRD_E_CANT_OPEN).</summary>
    private static T Retry<T>(Func<T> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (ExternalException) when (attempt < ClipboardRetries)
            {
                Thread.Sleep(30);
            }
        }
    }

    private static void Retry(Action action) => Retry(() =>
    {
        action();
        return true;
    });

    private readonly record struct Content(ClipboardFormat Format, byte[] Data, byte[] Hash, int Size);
}

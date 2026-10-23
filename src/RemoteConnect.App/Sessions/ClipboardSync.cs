using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Globalization;
using System.Security.Cryptography;
using RemoteConnect.Core.Files;
using RemoteConnect.Core.Protocol;
using RemoteConnect.Core.Session;
using RemoteConnect.Windows.Clipboard;
using RemoteConnect.Windows.Input;
using Serilog;

namespace RemoteConnect.App.Sessions;

/// <summary>
/// Синхронизация буфера обмена в рамках одной сессии (spec §5.6). Создаётся после «Разрешить»
/// и реагирует только на изменения после этого момента (FR-CLP-3). Работает на UI-потоке.
/// Файлы из Проводника передаются как набор виртуальных файлов (spec-file-clipboard): у себя — источник
/// (<see cref="FileTransferSource"/>), у собеседника — объект в буфере, читающий файлы по сети при вставке.
/// </summary>
public sealed class ClipboardSync : IDisposable
{
    private const int ClipboardRetries = 5;
    private static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(10);
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private readonly Connection _connection;
    private readonly Dispatcher _dispatcher;
    private readonly FileTransferSource _fileSource;
    private readonly FileTransferReceiver _fileReceiver;
    private readonly DispatcherTimer _statusTimer;
    private HwndSource? _window;
    private VirtualFileDataObject? _placedFiles;
    private uint _offeredSequence;
    private string? _notice;
    private DateTime _noticeUntil;
    private string _status = string.Empty;

    public ClipboardSync(Connection connection, Dispatcher dispatcher)
    {
        _connection = connection;
        _dispatcher = dispatcher;
        _fileSource = new FileTransferSource((message, background) => _connection.Send(message, background));
        _fileReceiver = new FileTransferReceiver(message => _connection.Send(message));
        _fileSource.TransfersChanged += () => _dispatcher.InvokeAsync(UpdateStatus);
        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => UpdateStatus(), dispatcher);
        _statusTimer.Stop();
    }

    /// <summary>Собеседник включил или выключил синхронизацию.</summary>
    public event Action<bool>? RemoteEnabledChanged;

    /// <summary>
    /// Строка для окна просмотра / плашки (FR-FCB-19, 20): прогресс передачи файлов или уведомление;
    /// пустая — показывать нечего. Вызывается на UI-потоке.
    /// </summary>
    public event Action<string>? StatusChanged;

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

        // Обрыв соединения сразу завершает чтения Проводника ошибкой (FR-FCB-14).
        _ = _connection.Completion.ContinueWith(_ => _fileReceiver.FailAll(), TaskScheduler.Default);
    }

    public void SetLocalEnabled(bool enabled)
    {
        Policy.LocalEnabled = enabled;
        _connection.Send(new ClipboardEnabled(enabled));
        if (!enabled)
        {
            RevokeFiles();
        }

        Log.Information("Буфер обмена: синхронизация {State} локально", enabled ? "включена" : "выключена");
    }

    /// <summary>Обрабатывает сообщения буфера от собеседника; можно вызывать с любого потока.</summary>
    public bool TryHandle(Message message)
    {
        switch (message)
        {
            case ClipboardEnabled enabled:
                Policy.RemoteEnabled = enabled.Enabled;
                _dispatcher.InvokeAsync(() =>
                {
                    if (!enabled.Enabled)
                    {
                        RevokeFiles();
                    }

                    RemoteEnabledChanged?.Invoke(enabled.Enabled);
                });
                return true;
            case ClipboardData data:
                _dispatcher.InvokeAsync(() => Apply(data));
                return true;
            case FileOffer offer:
                _dispatcher.InvokeAsync(() => PlaceFiles(offer));
                return true;
            default:
                // Части файлов обрабатываются прямо в цикле приёма, без UI-потока.
                return _fileSource.Handle(message) || _fileReceiver.Handle(message);
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

        _statusTimer.Stop();
        _fileSource.Dispose();
        _fileReceiver.FailAll();
        ClearPlacedFiles();
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

        // Наш же объект с файлами собеседника — не новое содержимое.
        if (_placedFiles is { } placed && OleClipboard.IsCurrent(placed))
        {
            return;
        }

        try
        {
            if (Retry(() => Clipboard.ContainsFileDropList()))
            {
                OfferLocalFiles();
                return;
            }

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
                // Новое содержимое заменяет набор файлов (FR-FCB-5); начатая вставка доделывается.
                _fileSource.Revoke(cancelTransfers: false);
                _connection.Send(new ClipboardData(c.Format, c.Data));
                Log.Debug("Буфер обмена: отправлено {Format}, {Bytes} байт", c.Format, c.Data.Length);
            }
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            Log.Debug(ex, "Буфер обмена: не удалось прочитать");
        }
    }

    /// <summary>
    /// Файлы скопированы в Проводнике (FR-FCB-1): набор строится в фоне (FR-FCB-4) и предлагается собеседнику.
    /// Одно копирование может прислать несколько уведомлений — их отсекает номер содержимого буфера.
    /// </summary>
    private void OfferLocalFiles()
    {
        var sequence = ClipboardNotifications.SequenceNumber;
        if (sequence == _offeredSequence)
        {
            return;
        }

        _offeredSequence = sequence;
        var paths = Retry(() => Clipboard.GetFileDropList()).Cast<string>().ToList();
        if (paths.Count == 0 || !Policy.ShouldSend(SHA256.HashData(BitConverter.GetBytes(sequence))))
        {
            return;
        }

        _ = Task.Run(() => FileOfferBuilder.Build(paths)).ContinueWith(
            task =>
            {
                // Пока строили, в буфер могли скопировать что-то ещё или сессия закончилась.
                if (_window is null || !_connection.IsOpen || ClipboardNotifications.SequenceNumber != sequence || !Policy.IsActive)
                {
                    return;
                }

                if (task.Exception is { } error)
                {
                    Log.Warning(error, "Файлы: ошибка построения набора");
                    return;
                }

                var result = task.Result;
                if (result.IsOk)
                {
                    var offer = _fileSource.Offer(result);
                    Log.Information("Файлы: предложено {Count} элементов, {Bytes} байт", offer.Entries.Count, result.TotalBytes);
                }
                else if (result.Status != FileOfferBuildStatus.Empty)
                {
                    Log.Information("Файлы: набор не предложен — {Status} {Problem}", result.Status, result.Problem);
                    ShowNotice("Файлы не будут доступны на другом ПК: " + Describe(result));
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Кладёт набор собеседника в буфер как виртуальные файлы (FR-FCB-6); имена проверяются заранее (FR-FCB-17).</summary>
    private void PlaceFiles(FileOffer offer)
    {
        if (_window is null || !Policy.IsActive)
        {
            return;
        }

        if (FileNameValidator.FindInvalid(offer.Entries) is { } invalid)
        {
            Log.Warning("Файлы: набор с другого ПК отклонён, недопустимый путь «{Path}»", invalid);
            ShowNotice($"Файлы с другого ПК отклонены: недопустимое имя «{invalid}»");
            return;
        }

        var entries = offer.Entries;
        var data = new VirtualFileDataObject(entries, index => _fileReceiver.Open(offer.OfferId, index, entries[index].Size));
        try
        {
            OleClipboard.Set(data);
            _placedFiles = data;
            Policy.OnApplied(SHA256.HashData(BitConverter.GetBytes(offer.OfferId)));
            Log.Information("Файлы: в буфере {Count} элементов с другого ПК", entries.Count);
        }
        catch (ExternalException ex)
        {
            Log.Warning(ex, "Файлы: не удалось поместить в буфер");
        }
    }

    /// <summary>Синхронизация выключена: набор недоступен ни у нас, ни у собеседника (FR-FCB-18).</summary>
    private void RevokeFiles()
    {
        _fileSource.Revoke();
        ClearPlacedFiles();
    }

    private void ClearPlacedFiles()
    {
        if (_placedFiles is { } placed)
        {
            _placedFiles = null;
            try
            {
                OleClipboard.ClearIfCurrent(placed);
            }
            catch (ExternalException ex)
            {
                Log.Debug(ex, "Файлы: не удалось очистить буфер");
            }
        }
    }

    private void ShowNotice(string text)
    {
        _notice = text;
        _noticeUntil = DateTime.UtcNow + NoticeDuration;
        UpdateStatus();
    }

    /// <summary>Прогресс передачи важнее уведомления; таймер работает, пока есть что показывать.</summary>
    private void UpdateStatus()
    {
        var transfers = _fileSource.ActiveTransfers;
        string status;
        if (transfers.Count > 0)
        {
            var first = transfers[0];
            var percent = first.Size == 0 ? 100 : (int)(first.Sent * 100 / first.Size);
            var count = transfers.Count > 1 ? $" ({transfers.Count})" : string.Empty;
            status = $"Передача файлов{count}: {first.Name}, {percent}%";
        }
        else if (_notice is not null && DateTime.UtcNow < _noticeUntil)
        {
            status = _notice;
        }
        else
        {
            _notice = null;
            status = string.Empty;
        }

        _statusTimer.IsEnabled = status.Length > 0 && _window is not null;
        if (status != _status)
        {
            _status = status;
            StatusChanged?.Invoke(status);
        }
    }

    private static string Describe(FileOfferBuildResult result) => result.Status switch
    {
        FileOfferBuildStatus.TooLarge => $"{FormatSize(result.TotalBytes)} — больше лимита {FormatSize(FileOfferBuilder.MaxTotalBytes)}",
        FileOfferBuildStatus.TooManyItems => $"больше {FileOfferBuilder.MaxEntries.ToString("N0", Russian)} файлов и папок",
        FileOfferBuildStatus.TimedOut => "слишком много папок, список не успел построиться",
        FileOfferBuildStatus.PathTooLong => $"слишком длинный путь «{result.Problem}»",
        _ => $"не удалось прочитать ({result.Problem})",
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Format(Russian, "{0:0.0} ГБ", bytes / (double)(1L << 30)),
        >= 1L << 20 => string.Format(Russian, "{0:0} МБ", bytes / (double)(1L << 20)),
        _ => string.Format(Russian, "{0:0} КБ", Math.Ceiling(bytes / 1024.0)),
    };

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

        // Прочие форматы не передаются.
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

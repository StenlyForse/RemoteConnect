using System.Diagnostics;
using RemoteConnect.Core.Imaging;
using RemoteConnect.Core.Protocol;
using RemoteConnect.Core.Session;
using RemoteConnect.Core.Streaming;
using RemoteConnect.Windows.Capture;
using RemoteConnect.Windows.Encoding;
using Serilog;

namespace RemoteConnect.App.Sessions;

/// <summary>
/// Цикл захват → поиск изменений → JPEG → отправка (FR-SCR-1..3, FR-SCR-5) с подстройкой под канал (spec-v0.5, часть A):
/// качество, число кадров в пути и частоту задаёт <see cref="StreamRateController"/>; в тиках без изменений
/// мутные области дорисовываются в хорошем качестве (<see cref="RefineTracker"/>).
/// </summary>
public sealed class ScreenStreamer(Connection connection)
{
    private const int MaxBandHeight = 256;

    /// <summary>Кадр больше этого уходит двумя частями вплотную — по ним регулятор оценивает канал.</summary>
    private const int SplitThresholdBytes = 8 * 1024;

    /// <summary>Доля экрана, дорисовываемая за один кадр: на медленном канале дорисовка не задерживает следующее движение.</summary>
    private const double RefineAreaFraction = 0.25;

    private static readonly TimeSpan MonitorCheckInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StatsLogInterval = TimeSpan.FromSeconds(10);

    private readonly StreamRateController _rate = new(TimeProvider.System);
    private readonly RefineTracker _refine = new(TimeProvider.System);
    private volatile int _requestedMonitor;
    private volatile DisplayMonitor? _currentMonitor;
    private int _frameNumber;

    /// <summary>Транслируемый монитор — по нему пересчитываются координаты мыши.</summary>
    public DisplayMonitor? CurrentMonitor => _currentMonitor;

    public void OnFrameAck(int frameNumber) => _rate.OnFrameAcked(frameNumber);

    public void RequestMonitor(int index) => _requestedMonitor = index;

    /// <summary>Управляющий выбрал режим (FR-ADP-6).</summary>
    public void SetMode(StreamModeKind mode)
    {
        _rate.SetMode(mode);
        Log.Information("Режим изображения: {Mode}", mode);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var capturer = new GdiScreenCapturer();
        IReadOnlyList<DisplayMonitor> monitors = [];
        byte[]? previous = null;
        var current = Array.Empty<byte>();
        var activeIndex = -1;
        var lastMonitorCheck = Stopwatch.StartNew();
        var lastStatsLog = Stopwatch.StartNew();

        while (!ct.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();

            if (activeIndex != _requestedMonitor || lastMonitorCheck.Elapsed > MonitorCheckInterval)
            {
                lastMonitorCheck.Restart();
                var fresh = DisplayMonitors.GetAll();
                var index = Math.Clamp(_requestedMonitor, 0, fresh.Count - 1);
                if (index != activeIndex || !fresh.SequenceEqual(monitors))
                {
                    monitors = fresh;
                    activeIndex = index;
                    _requestedMonitor = index;
                    var monitor = monitors[index];
                    _currentMonitor = monitor;
                    current = new byte[monitor.Width * monitor.Height * 4];
                    previous = null;
                    _refine.Reset(monitor.Width, monitor.Height);
                    _rate.ForgetPending();
                    connection.Send(new ScreenInfo(
                        monitors.Select(m => new MonitorDescriptor(m.DeviceName, m.Width, m.Height, m.IsPrimary)).ToList(),
                        index,
                        monitor.Width,
                        monitor.Height));
                    Log.Information("Трансляция монитора {Index}: {Width}x{Height}", index, monitor.Width, monitor.Height);
                }
            }

            await _rate.WaitForSlotAsync(ct);
            try
            {
                var monitor = _currentMonitor!;
                var processing = Stopwatch.StartNew();
                capturer.Capture(monitor, current);
                var stride = monitor.Width * 4;
                var rects = previous is null
                    ? [TileDiffer.Full(monitor.Width, monitor.Height)]
                    : TileDiffer.FindDirtyRects(previous, current, monitor.Width, monitor.Height, stride);

                if (rects.Count > 0)
                {
                    var quality = _rate.Quality;
                    Send(Encode(current, stride, rects, quality), quality);
                    _rate.OnFrameProcessed(processing.Elapsed);
                    _refine.OnSent(rects, quality);
                    (previous, current) = (current, previous ?? new byte[current.Length]);
                }
                else
                {
                    // Экран не менялся — дорисовываем то, что было передано мутно (FR-ADP-5). Переданное хорошо не шлётся (FR-ADP-8).
                    var quality = _rate.Profile.RefineQuality;
                    var refine = _refine.TakeRefinement(quality, RefineAreaFraction);
                    if (refine.Count > 0)
                    {
                        Send(Encode(current, stride, refine, quality), quality);
                        _refine.OnSent(refine, quality);
                    }
                }
            }
            catch (ScreenCaptureException ex)
            {
                Log.Debug("Захват недоступен: {Message}", ex.Message);
                await Task.Delay(500, ct);
            }

            if (lastStatsLog.Elapsed > StatsLogInterval)
            {
                lastStatsLog.Restart();
                Log.Debug(
                    "Изображение: качество {Quality}, {Fps:F1} к/с, кадров в пути {InFlight}, задержка {Delay} мс, канал {Capacity} КБ/с",
                    _rate.Quality, _rate.Fps, _rate.MaxFramesInFlight, (int?)_rate.Delay?.TotalMilliseconds, (int?)(_rate.Capacity / 1024));
            }

            var remaining = _rate.FrameInterval - Stopwatch.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, ct);
            }
        }
    }

    private static List<EncodedTile> Encode(byte[] frame, int stride, List<PixelRect> rects, int quality) =>
        SplitIntoBands(rects)
            .AsParallel()
            .AsOrdered()
            .Select(r => new EncodedTile(r.X, r.Y, r.Width, r.Height, JpegTileCodec.Encode(frame, stride, r, quality)))
            .ToList();

    /// <summary>
    /// Отправляет кадр одной или двумя частями. Регулятор узнаёт о кадре до отправки — иначе подтверждение
    /// на быстром канале могло бы прийти раньше.
    /// </summary>
    private void Send(List<EncodedTile> tiles, int quality)
    {
        var total = tiles.Sum(t => t.Data.Length);
        var split = tiles.Count >= 2 && total > SplitThresholdBytes ? SplitPoint(tiles, total) : tiles.Count;
        List<List<EncodedTile>> parts = split < tiles.Count ? [tiles[..split], tiles[split..]] : [tiles];

        var numbered = parts.Select(p => (Number: ++_frameNumber, Tiles: p)).ToList();
        _rate.OnFrameSent(numbered.Select(p => (p.Number, p.Tiles.Sum(t => t.Data.Length))).ToList());
        for (var i = 0; i < numbered.Count; i++)
        {
            connection.Send(new FrameTiles(numbered[i].Number, (byte)quality, i == numbered.Count - 1, numbered[i].Tiles));
        }
    }

    /// <summary>Индекс, делящий плитки примерно пополам по байтам (обе части непустые).</summary>
    private static int SplitPoint(List<EncodedTile> tiles, int total)
    {
        var sum = 0;
        for (var i = 0; i < tiles.Count - 1; i++)
        {
            sum += tiles[i].Data.Length;
            if (sum * 2 >= total)
            {
                return i + 1;
            }
        }

        return tiles.Count - 1;
    }

    /// <summary>Делит высокие прямоугольники на полосы, чтобы кодировать их параллельно.</summary>
    private static IEnumerable<PixelRect> SplitIntoBands(List<PixelRect> rects)
    {
        foreach (var rect in rects)
        {
            for (var y = 0; y < rect.Height; y += MaxBandHeight)
            {
                yield return rect with { Y = rect.Y + y, Height = Math.Min(MaxBandHeight, rect.Height - y) };
            }
        }
    }
}

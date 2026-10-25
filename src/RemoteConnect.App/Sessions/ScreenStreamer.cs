using System.Diagnostics;
using RemoteConnect.Core.Imaging;
using RemoteConnect.Core.Protocol;
using RemoteConnect.Core.Session;
using RemoteConnect.Windows.Capture;
using RemoteConnect.Windows.Encoding;
using Serilog;

namespace RemoteConnect.App.Sessions;

/// <summary>
/// Цикл захват → поиск изменений → JPEG → отправка (FR-SCR-1..3, FR-SCR-5).
/// Не больше <see cref="MaxFramesInFlight"/> неподтверждённых кадров: на медленном канале
/// кадры пропускаются, а не копятся в очереди (plan.md §3.4).
/// </summary>
public sealed class ScreenStreamer(Connection connection, int jpegQuality)
{
    public const int MaxFramesInFlight = 2;
    private const int MaxBandHeight = 256;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MonitorCheckInterval = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _credits = new(MaxFramesInFlight, MaxFramesInFlight);
    private volatile int _requestedMonitor;
    private volatile DisplayMonitor? _currentMonitor;

    /// <summary>Транслируемый монитор — по нему пересчитываются координаты мыши.</summary>
    public DisplayMonitor? CurrentMonitor => _currentMonitor;

    public void OnFrameAck()
    {
        try
        {
            _credits.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public void RequestMonitor(int index) => _requestedMonitor = index;

    public async Task RunAsync(CancellationToken ct)
    {
        using var capturer = new GdiScreenCapturer();
        IReadOnlyList<DisplayMonitor> monitors = [];
        byte[]? previous = null;
        var current = Array.Empty<byte>();
        var activeIndex = -1;
        var frameNumber = 0;
        var lastMonitorCheck = Stopwatch.StartNew();

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
                    connection.Send(new ScreenInfo(
                        monitors.Select(m => new MonitorDescriptor(m.DeviceName, m.Width, m.Height, m.IsPrimary)).ToList(),
                        index,
                        monitor.Width,
                        monitor.Height));
                    Log.Information("Трансляция монитора {Index}: {Width}x{Height}", index, monitor.Width, monitor.Height);
                }
            }

            await _credits.WaitAsync(ct);
            var sent = false;
            try
            {
                var monitor = _currentMonitor!;
                capturer.Capture(monitor, current);
                var stride = monitor.Width * 4;
                var rects = previous is null
                    ? [TileDiffer.Full(monitor.Width, monitor.Height)]
                    : TileDiffer.FindDirtyRects(previous, current, monitor.Width, monitor.Height, stride);

                if (rects.Count > 0)
                {
                    var frame = current;
                    var tiles = SplitIntoBands(rects)
                        .AsParallel()
                        .AsOrdered()
                        .Select(r => new EncodedTile(r.X, r.Y, r.Width, r.Height, JpegTileCodec.Encode(frame, stride, r, jpegQuality)))
                        .ToList();
                    connection.Send(new FrameTiles(++frameNumber, (byte)jpegQuality, tiles));
                    sent = true;
                    (previous, current) = (current, previous ?? new byte[current.Length]);
                }
            }
            catch (ScreenCaptureException ex)
            {
                Log.Debug("Захват недоступен: {Message}", ex.Message);
                await Task.Delay(500, ct);
            }
            finally
            {
                if (!sent)
                {
                    OnFrameAck();
                }
            }

            var remaining = FrameInterval - Stopwatch.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, ct);
            }
        }
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

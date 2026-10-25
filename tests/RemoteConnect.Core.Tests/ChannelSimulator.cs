using Microsoft.Extensions.Time.Testing;
using RemoteConnect.Core.Streaming;

namespace RemoteConnect.Core.Tests;

/// <summary>
/// Дискретная имитация трансляции через канал (plan-v0.5 §4): шаг 1 мс, без сети и реального времени.
/// Канал — очередь FIFO с пропускной способностью и задержкой в одну сторону; размер кадра зависит от качества
/// и доли изменившегося экрана; подтверждение идёт обратно с той же задержкой.
/// </summary>
internal sealed class ChannelSimulator
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(1);

    private readonly FakeTimeProvider _time;
    private readonly StreamRateController _controller;
    private readonly List<(TimeSpan AckAt, int Frame)> _acks = [];
    private TimeSpan _now;
    private TimeSpan _linkFreeAt;
    private TimeSpan _nextCapture;
    private int _frame;

    public ChannelSimulator(FakeTimeProvider time, StreamRateController controller)
    {
        _time = time;
        _controller = controller;
    }

    /// <summary>Пропускная способность в мегабитах в секунду.</summary>
    public double Mbps { get; set; } = 10;

    /// <summary>Задержка в одну сторону; пинг — вдвое больше.</summary>
    public TimeSpan OneWayDelay { get; set; } = TimeSpan.FromMilliseconds(20);

    /// <summary>Время захвата и кодирования кадра на управляемой стороне.</summary>
    public TimeSpan EncodeCost { get; set; } = TimeSpan.FromMilliseconds(10);

    /// <summary>Доля экрана 1920×1080, меняющаяся в каждом кадре (перетаскивание окна, прокрутка — около 0,15).</summary>
    public double ChangedFraction { get; set; } = 0.15;

    /// <summary>Подтверждённые кадры: когда отправлен, сколько шёл туда и обратно, с каким качеством.</summary>
    public List<(TimeSpan SentAt, TimeSpan Rtt, int Quality)> Frames { get; } = [];

    public TimeSpan Ping => OneWayDelay * 2;

    public void Run(TimeSpan duration)
    {
        var end = _now + duration;
        while (_now < end)
        {
            foreach (var ack in _acks.Where(a => a.AckAt <= _now).ToList())
            {
                _acks.Remove(ack);
                _controller.OnFrameAcked(ack.Frame);
            }

            if (_now >= _nextCapture && _controller.HasSlot && ChangedFraction > 0)
            {
                SendFrame();
            }

            _now += Step;
            _time.Advance(Step);
        }
    }

    /// <summary>Размер кадра по модели JPEG рабочего стола: около 1 бита на пиксель при качестве 70.</summary>
    public static int FrameBytes(int quality, double changedFraction)
    {
        double[] q = [20, 30, 40, 50, 60, 70, 80, 85, 90];
        double[] bitsPerPixel = [0.35, 0.45, 0.55, 0.65, 0.8, 1.0, 1.35, 1.6, 2.0];
        var i = Math.Clamp(Array.FindLastIndex(q, v => v <= quality), 0, q.Length - 2);
        var t = Math.Clamp((quality - q[i]) / (q[i + 1] - q[i]), 0, 1);
        var bpp = bitsPerPixel[i] + ((bitsPerPixel[i + 1] - bitsPerPixel[i]) * t);
        return (int)(1920 * 1080 * changedFraction * bpp / 8);
    }

    private void SendFrame()
    {
        _nextCapture = _now + _controller.FrameInterval;
        var quality = _controller.Quality;
        var bytes = FrameBytes(quality, ChangedFraction);
        _controller.OnFrameProcessed(EncodeCost);

        // Кадр больше 8 КБ уходит двумя частями вплотную (как в ScreenStreamer); каждая часть подтверждается отдельно,
        // управляющий декодирует части по очереди.
        int[] parts = bytes > 8 * 1024 ? [bytes / 2, bytes - (bytes / 2)] : [bytes];
        var numbers = parts.Select(_ => ++_frame).ToArray();
        _controller.OnFrameSent(numbers.Zip(parts, (n, b) => (n, b)).ToList());

        var start = _now > _linkFreeAt ? _now : _linkFreeAt;
        var decodedAt = TimeSpan.Zero;
        var ackAt = TimeSpan.Zero;
        for (var i = 0; i < parts.Length; i++)
        {
            _linkFreeAt = start + TimeSpan.FromSeconds(parts[i] * 8 / (Mbps * 1_000_000));
            start = _linkFreeAt;
            var arrived = _linkFreeAt + OneWayDelay;
            decodedAt = (arrived > decodedAt ? arrived : decodedAt) + TimeSpan.FromMilliseconds(3);
            ackAt = decodedAt + OneWayDelay;
            _acks.Add((ackAt, numbers[i]));
        }

        Frames.Add((_now, ackAt - _now, quality));
    }
}

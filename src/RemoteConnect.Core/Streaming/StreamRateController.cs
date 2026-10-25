using RemoteConnect.Core.Protocol;

namespace RemoteConnect.Core.Streaming;

/// <summary>Границы и стартовые значения режима подстройки (plan-v0.5 §2).</summary>
public sealed record StreamProfile(
    int MinQuality,
    int MaxQuality,
    int StartQuality,
    double MinFps,
    double MaxFps,
    double StartFps,
    TimeSpan CongestionThreshold,
    int RefineQuality)
{
    public static StreamProfile For(StreamModeKind mode) => mode switch
    {
        StreamModeKind.BestQuality => new(75, 90, 85, 2, 30, 20, TimeSpan.FromMilliseconds(400), 90),
        StreamModeKind.Faster => new(20, 55, 40, 10, 30, 20, TimeSpan.FromMilliseconds(100), 60),
        _ => new(30, 85, 60, 5, 30, 20, TimeSpan.FromMilliseconds(150), 80),
    };
}

/// <summary>
/// Подстройка качества JPEG, числа кадров в пути и частоты кадров под канал (spec-v0.5, FR-ADP-1…4, NFR-ADP-1).
/// <para>
/// Кадр отправляется одной или двумя частями (сообщения <c>FrameTiles</c> со своими номерами) сразу друг за другом.
/// Интервал между подтверждениями частей — время передачи второй части: из него оценивается пропускная способность
/// канала («замер парой»). Время кадра в пути раскладывается на базу (пинг и обработка: минимум за 10 с по кадрам,
/// ушедшим в пустой канал, без времени их передачи) и задержку — передачу кадра плюс очередь. Её и ограничивает регулятор.
/// </para>
/// <para>
/// Задержка выше порога режима — сначала снижается качество, затем остаётся один кадр в пути, затем падает частота;
/// задержка мала секунду — по шагу в секунду возвращаются частота, кадры в пути, качество. Потокобезопасен.
/// </para>
/// </summary>
public sealed class StreamRateController
{
    /// <summary>Не чаще 30 кадров/с; реальный минимум интервала ещё ограничен временем кодирования.</summary>
    public static readonly TimeSpan MinFrameInterval = TimeSpan.FromMilliseconds(1000.0 / 30);

    private static readonly TimeSpan HistoryWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IncreaseStep = TimeSpan.FromSeconds(1);
    private const int QualityDecrease = 10;
    private const int QualityIncrease = 5;
    private const double FpsDecreaseFactor = 1.5;
    private const double FpsIncreaseFactor = 1.25;
    private const double SmoothingFactor = 0.3;

    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Dictionary<int, Frame> _partToFrame = [];
    private readonly HashSet<Frame> _pending = [];
    private readonly Queue<(long At, TimeSpan Rtt, int Bytes)> _baseHistory = new();
    private readonly Queue<(long At, double BytesPerSecond)> _capacityHistory = new();
    private readonly SemaphoreSlim _ackSignal = new(0);

    private StreamProfile _profile;
    private int _quality;
    private double _fps;
    private int _maxInFlight = 2;
    private TimeSpan? _smoothedRtt;
    private TimeSpan? _processingCost;
    private long _lastDecrease;
    private long _lastIncrease;
    private long? _goodSince;

    public StreamRateController(TimeProvider time, StreamModeKind mode = StreamModeKind.Auto)
    {
        _time = time;
        _profile = StreamProfile.For(mode);
        Mode = mode;
        _quality = _profile.StartQuality;
        _fps = _profile.StartFps;
        _lastDecrease = _lastIncrease = time.GetTimestamp();
    }

    public StreamModeKind Mode { get; private set; }

    public StreamProfile Profile => Locked(() => _profile);

    public int Quality => Locked(() => _quality);

    public double Fps => Locked(() => _fps);

    /// <summary>Сколько кадров может быть отправлено без подтверждения: 2, на перегруженном канале — 1.</summary>
    public int MaxFramesInFlight => Locked(() => _maxInFlight);

    public bool HasSlot => Locked(() => _pending.Count < _maxInFlight);

    /// <summary>Интервал между захватами: по частоте режима, но не меньше, чем успевает кодирование (NFR-ADP-1).</summary>
    public TimeSpan FrameInterval => Locked(() =>
    {
        var byFps = TimeSpan.FromSeconds(1 / _fps);
        return byFps > MinIntervalByCost ? byFps : MinIntervalByCost;
    });

    /// <summary>Сглаженная задержка сверх базы (передача кадра и очередь) — для журнала.</summary>
    public TimeSpan? Delay => Locked(() => _smoothedRtt - BaseRtt);

    /// <summary>Оценка пропускной способности канала, байт/с (максимум замеров за 10 с) — для журнала.</summary>
    public double? Capacity => Locked(() => CapacityEstimate);

    private TimeSpan MinIntervalByCost =>
        _processingCost is { } cost && cost * 1.25 > MinFrameInterval ? cost * 1.25 : MinFrameInterval;

    private double? CapacityEstimate => _capacityHistory.Count == 0 ? null : _capacityHistory.Max(e => e.BytesPerSecond);

    /// <summary>Время передачи вычитается при расчёте, по текущей оценке канала: замер мог быть сделан до первой оценки.</summary>
    private TimeSpan? BaseRtt
    {
        get
        {
            if (_baseHistory.Count == 0)
            {
                return null;
            }

            var capacity = CapacityEstimate;
            var min = _baseHistory.Min(e => capacity is { } c ? e.Rtt - TimeSpan.FromSeconds(e.Bytes / c) : e.Rtt);
            return min > TimeSpan.Zero ? min : TimeSpan.Zero;
        }
    }

    /// <summary>Управляющий выбрал режим (FR-ADP-6): границы меняются, параметры — к стартовым значениям нового режима.</summary>
    public void SetMode(StreamModeKind mode)
    {
        lock (_lock)
        {
            Mode = mode;
            _profile = StreamProfile.For(mode);
            _quality = _profile.StartQuality;
            _fps = _profile.StartFps;
            _maxInFlight = 2;
            _goodSince = null;
            _lastIncrease = _time.GetTimestamp();
        }
    }

    /// <summary>Сколько заняли захват, поиск изменений и кодирование кадра.</summary>
    public void OnFrameProcessed(TimeSpan cost)
    {
        lock (_lock)
        {
            _processingCost = _processingCost is { } prev ? Lerp(prev, cost) : cost;
        }
    }

    /// <summary>Кадр отправлен частями (номер сообщения, байт) — сразу друг за другом.</summary>
    public void OnFrameSent(IReadOnlyList<(int Number, int Bytes)> parts)
    {
        lock (_lock)
        {
            var frame = new Frame(_time.GetTimestamp(), parts.Select(p => p.Bytes).ToArray(), sentIdle: _pending.Count == 0);
            foreach (var part in parts)
            {
                _partToFrame[part.Number] = frame;
            }

            _pending.Add(frame);
        }
    }

    public void OnFrameSent(int number, int bytes) => OnFrameSent([(number, bytes)]);

    public void OnFrameAcked(int number)
    {
        lock (_lock)
        {
            if (!_partToFrame.Remove(number, out var frame) || !_pending.Contains(frame))
            {
                return;
            }

            var now = _time.GetTimestamp();
            var index = frame.Acked++;
            if (index == 0 && frame.SentIdle)
            {
                // Первая часть кадра, ушедшего в пустой канал: её время в пути без её передачи — замер базы.
                // Если перед ней ещё шёл предыдущий кадр, ожидание попало бы в базу и спрятало очередь.
                _baseHistory.Enqueue((now, _time.GetElapsedTime(frame.SentAt, now), frame.Bytes[0]));
            }
            else if (index > 0)
            {
                // Следующая часть ушла вплотную за предыдущей: интервал подтверждений — время её передачи.
                var gap = _time.GetElapsedTime(frame.LastAckAt, now);
                if (gap > TimeSpan.Zero)
                {
                    _capacityHistory.Enqueue((now, frame.Bytes[index] / gap.TotalSeconds));
                }
            }

            frame.LastAckAt = now;
            Trim(_baseHistory, e => e.At, now, keepLast: true);
            Trim(_capacityHistory, e => e.At, now, keepLast: true);

            if (frame.Acked < frame.Bytes.Length)
            {
                return;
            }

            _pending.Remove(frame);
            var frameRtt = _time.GetElapsedTime(frame.SentAt, now);
            _smoothedRtt = _smoothedRtt is { } prev ? Lerp(prev, frameRtt) : frameRtt;
            Adjust(now, _smoothedRtt.Value - (BaseRtt ?? TimeSpan.Zero));
        }

        _ackSignal.Release();
    }

    /// <summary>Ждёт, пока можно отправить следующий кадр (неподтверждённых меньше <see cref="MaxFramesInFlight"/>).</summary>
    public async Task WaitForSlotAsync(CancellationToken ct)
    {
        while (!HasSlot)
        {
            await _ackSignal.WaitAsync(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Сбрасывает неподтверждённые кадры (смена монитора) — чтобы не ждать их вечно.</summary>
    public void ForgetPending()
    {
        lock (_lock)
        {
            _pending.Clear();
            _partToFrame.Clear();
        }

        _ackSignal.Release();
    }

    private void Adjust(long now, TimeSpan delay)
    {
        if (delay > _profile.CongestionThreshold)
        {
            _goodSince = null;

            // Ждём, пока подействует предыдущее снижение: кадры, отправленные после него, приходят через ~rtt.
            if (_time.GetElapsedTime(_lastDecrease, now) < _smoothedRtt!.Value)
            {
                return;
            }

            _lastDecrease = now;
            if (_quality > _profile.MinQuality)
            {
                _quality = Math.Max(_profile.MinQuality, _quality - QualityDecrease);
            }
            else if (_maxInFlight > 1)
            {
                _maxInFlight = 1;
            }
            else
            {
                _fps = Math.Max(_profile.MinFps, _fps / FpsDecreaseFactor);
            }

            return;
        }

        if (delay >= _profile.CongestionThreshold / 3)
        {
            _goodSince = null;
            return;
        }

        _goodSince ??= now;
        if (_time.GetElapsedTime(_goodSince.Value, now) < IncreaseStep || _time.GetElapsedTime(_lastIncrease, now) < IncreaseStep)
        {
            return;
        }

        _lastIncrease = now;
        var maxFps = Math.Min(_profile.MaxFps, 1 / MinIntervalByCost.TotalSeconds);

        // Возврат — в обратном порядке снижения: частота, кадры в пути, качество.
        if (_fps < maxFps - 0.01)
        {
            _fps = Math.Min(maxFps, _fps * FpsIncreaseFactor);
        }
        else if (_maxInFlight < 2)
        {
            _maxInFlight = 2;
        }
        else if (_quality < _profile.MaxQuality)
        {
            _quality = Math.Min(_profile.MaxQuality, _quality + QualityIncrease);
        }
    }

    /// <summary>Убирает замеры старше 10 с; последний остаётся, пока нет нового (на постоянно занятом канале).</summary>
    private void Trim<T>(Queue<T> queue, Func<T, long> at, long now, bool keepLast)
    {
        while (queue.Count > (keepLast ? 1 : 0) && _time.GetElapsedTime(at(queue.Peek()), now) > HistoryWindow)
        {
            queue.Dequeue();
        }
    }

    private T Locked<T>(Func<T> get)
    {
        lock (_lock)
        {
            return get();
        }
    }

    private static TimeSpan Lerp(TimeSpan previous, TimeSpan sample) =>
        previous + ((sample - previous) * SmoothingFactor);

    private sealed class Frame(long sentAt, int[] bytes, bool sentIdle)
    {
        public long SentAt { get; } = sentAt;

        public bool SentIdle { get; } = sentIdle;

        public int[] Bytes { get; } = bytes;

        public int Acked { get; set; }

        public long LastAckAt { get; set; }
    }
}

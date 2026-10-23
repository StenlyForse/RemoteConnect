namespace RemoteConnect.Core.Session;

/// <summary>
/// Авто-остановка ожидания хоста (FR-NET-11): срабатывает, если за отведённое время не началась сессия.
/// На время сессии ставится на паузу и после неё отсчитывается заново.
/// </summary>
public sealed class AutoStopTimer(TimeProvider time, TimeSpan timeout, Action onElapsed) : IDisposable
{
    private readonly Lock _lock = new();
    private ITimer? _timer;
    private DateTimeOffset? _deadline;

    /// <summary>Сколько осталось до остановки; null — таймер не идёт.</summary>
    public TimeSpan? Remaining
    {
        get
        {
            lock (_lock)
            {
                return _deadline is { } d ? Max(d - time.GetUtcNow(), TimeSpan.Zero) : null;
            }
        }
    }

    /// <summary>Запуск или перезапуск отсчёта с полного интервала.</summary>
    public void Restart()
    {
        if (timeout <= TimeSpan.Zero)
        {
            return;
        }

        lock (_lock)
        {
            _timer?.Dispose();
            _deadline = time.GetUtcNow() + timeout;
            _timer = time.CreateTimer(_ => Fire(), null, timeout, Timeout.InfiniteTimeSpan);
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            _deadline = null;
        }
    }

    public void Dispose() => Pause();

    private void Fire()
    {
        lock (_lock)
        {
            if (_deadline is null)
            {
                return;
            }

            _timer?.Dispose();
            _timer = null;
            _deadline = null;
        }

        onElapsed();
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

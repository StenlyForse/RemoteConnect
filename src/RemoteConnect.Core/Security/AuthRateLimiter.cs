using System.Net;

namespace RemoteConnect.Core.Security;

public sealed record BlockedAddress(IPAddress Address, TimeSpan Remaining);

public readonly record struct AuthFailureOutcome(bool IsBlocked, int AttemptsLeft, TimeSpan BlockedFor);

/// <summary>
/// Ограничение попыток ввода пароля по IP (FR-SEC-4, FR-SEC-9).
/// Учитываются только неверные пароли; блокировка снимается автоматически или вручную.
/// </summary>
public sealed class AuthRateLimiter(TimeProvider time, int maxFailures = 5, TimeSpan? blockDuration = null)
{
    private readonly TimeSpan _blockDuration = blockDuration ?? TimeSpan.FromMinutes(5);
    private readonly Dictionary<IPAddress, Entry> _entries = [];
    private readonly Lock _lock = new();

    public event EventHandler? Changed;

    public int MaxFailures => maxFailures;

    public bool IsBlocked(IPAddress address, out TimeSpan remaining)
    {
        address = Normalize(address);
        lock (_lock)
        {
            remaining = RemainingLocked(address);
            return remaining > TimeSpan.Zero;
        }
    }

    public AuthFailureOutcome RegisterFailure(IPAddress address)
    {
        address = Normalize(address);
        AuthFailureOutcome outcome;
        lock (_lock)
        {
            var remaining = RemainingLocked(address);
            if (remaining > TimeSpan.Zero)
            {
                return new AuthFailureOutcome(true, 0, remaining);
            }

            var entry = _entries.GetValueOrDefault(address);
            var failures = entry.Failures + 1;
            if (failures >= maxFailures)
            {
                _entries[address] = new Entry(0, time.GetUtcNow() + _blockDuration);
                outcome = new AuthFailureOutcome(true, 0, _blockDuration);
            }
            else
            {
                _entries[address] = new Entry(failures, null);
                outcome = new AuthFailureOutcome(false, maxFailures - failures, TimeSpan.Zero);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return outcome;
    }

    public void RegisterSuccess(IPAddress address)
    {
        address = Normalize(address);
        lock (_lock)
        {
            _entries.Remove(address);
        }
    }

    public bool Unblock(IPAddress address)
    {
        address = Normalize(address);
        bool removed;
        lock (_lock)
        {
            removed = _entries.Remove(address);
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    public IReadOnlyList<BlockedAddress> GetBlocked()
    {
        lock (_lock)
        {
            return _entries.Keys
                .Select(a => new BlockedAddress(a, RemainingLocked(a)))
                .Where(b => b.Remaining > TimeSpan.Zero)
                .OrderByDescending(b => b.Remaining)
                .ToList();
        }
    }

    private TimeSpan RemainingLocked(IPAddress address)
    {
        if (!_entries.TryGetValue(address, out var entry) || entry.BlockedUntil is not { } until)
        {
            return TimeSpan.Zero;
        }

        var remaining = until - time.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            _entries.Remove(address);
            return TimeSpan.Zero;
        }

        return remaining;
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private readonly record struct Entry(int Failures, DateTimeOffset? BlockedUntil);
}

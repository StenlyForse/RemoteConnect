using System.Security.Cryptography;
using RemoteConnect.Core.Protocol;

namespace RemoteConnect.Core.Session;

/// <summary>
/// Решения синхронизации буфера обмена (spec §5.6): включена ли она у обеих сторон (FR-CLP-4),
/// лимиты (FR-CLP-6), дедупликация и подавление «эха» после применения полученных данных (FR-CLP-5).
/// Потокобезопасна.
/// </summary>
public sealed class ClipboardSyncPolicy(TimeProvider time)
{
    public const int MaxTextChars = 1_000_000;
    public const int MaxImageBytes = 8 * 1024 * 1024;

    /// <summary>
    /// После записи полученных данных в буфер Windows присылает уведомление об изменении;
    /// изменения в этом окне считаются «эхом» и не отправляются обратно.
    /// </summary>
    public static readonly TimeSpan EchoWindow = TimeSpan.FromSeconds(1);

    private readonly Lock _lock = new();
    private byte[]? _lastHash;
    private long? _appliedAt;

    public bool LocalEnabled { get; set; } = true;

    public bool RemoteEnabled { get; set; } = true;

    public bool IsActive => LocalEnabled && RemoteEnabled;

    public static byte[] Hash(ClipboardFormat format, ReadOnlySpan<byte> content)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData([(byte)format]);
        sha.AppendData(content);
        return sha.GetHashAndReset();
    }

    public static bool WithinLimits(ClipboardFormat format, int size) => format switch
    {
        ClipboardFormat.Text => size <= MaxTextChars,
        ClipboardFormat.Image => size <= MaxImageBytes,
        _ => false,
    };

    /// <summary>Локальный буфер изменился: отправлять ли содержимое с хешем <paramref name="hash"/>.</summary>
    public bool ShouldSend(byte[] hash)
    {
        lock (_lock)
        {
            if (_lastHash is not null && hash.AsSpan().SequenceEqual(_lastHash))
            {
                return false;
            }

            _lastHash = hash;
            if (_appliedAt is { } applied && time.GetElapsedTime(applied) < EchoWindow)
            {
                // Windows могла немного изменить данные при записи (например, картинку) — это всё ещё эхо.
                return false;
            }

            return IsActive;
        }
    }

    /// <summary>Применять ли полученные данные.</summary>
    public bool ShouldApply(ClipboardFormat format, int size) => IsActive && WithinLimits(format, size);

    /// <summary>Полученные данные записаны в локальный буфер.</summary>
    public void OnApplied(byte[] hash)
    {
        lock (_lock)
        {
            _lastHash = hash;
            _appliedAt = time.GetTimestamp();
        }
    }
}

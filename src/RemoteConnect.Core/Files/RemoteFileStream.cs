using RemoteConnect.Core.Protocol;

namespace RemoteConnect.Core.Files;

/// <summary>Источник ответил ошибкой на запрос файла.</summary>
public sealed class RemoteFileException(FileErrorCode code)
    : IOException($"Файл на другом ПК недоступен: {code}")
{
    public FileErrorCode Code { get; } = code;
}

/// <summary>
/// Содержимое одного файла с другого ПК для Проводника (FR-FCB-7, 11, 14): только последовательное чтение.
/// <list type="bullet">
/// <item><c>FileOpen</c> отправляется при первом чтении — копирование в буфер само по себе ничего не скачивает.</item>
/// <item><see cref="Read(byte[], int, int)"/> блокируется до прихода данных, но не дольше <see cref="DataTimeout"/>.</item>
/// <item>По мере чтения отправляется <c>FileAck</c> — источник досылает следующую часть окна.</item>
/// <item>Закрытие раньше конца (отмена в Проводнике) отправляет <c>FileCancel</c>.</item>
/// </list>
/// Вызовы <c>Read</c> приходят на потоки RPC; данные — из цикла приёма соединения.
/// </summary>
public sealed class RemoteFileStream : Stream
{
    public static readonly TimeSpan DefaultDataTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Подтверждать прочитанное не реже, чем каждые столько байт (четверть окна источника).</summary>
    public const int AckStepBytes = FileTransferSource.WindowBytes / 4;

    private readonly FileTransferReceiver _owner;
    private readonly int _offerId;
    private readonly int _index;
    private readonly long _size;
    private readonly object _lock = new();
    private readonly Queue<byte[]> _chunks = new();
    private int _chunkOffset;
    private long _received;
    private long _consumed;
    private long _acked;
    private bool _opened;
    private bool _ended;

    /// <summary>Источник больше ничего не пришлёт (конец, ошибка или обрыв) — отменять нечего.</summary>
    private bool _sourceDone;
    private bool _disposed;
    private Exception? _error;

    internal RemoteFileStream(FileTransferReceiver owner, int requestId, int offerId, int index, long size, TimeSpan dataTimeout)
    {
        _owner = owner;
        RequestId = requestId;
        _offerId = offerId;
        _index = index;
        _size = size;
        DataTimeout = dataTimeout;
    }

    public int RequestId { get; }

    public TimeSpan DataTimeout { get; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _size;

    public override long Position
    {
        get => Interlocked.Read(ref _consumed);
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        long? ackToSend = null;
        int copied;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_opened)
            {
                _opened = true;
                if (_size == 0)
                {
                    // Пустой файл: запрашивать нечего.
                    _ended = true;
                    _sourceDone = true;
                }
                else
                {
                    _owner.Send(new FileOpen(RequestId, _offerId, _index));
                }
            }

            var deadline = DateTime.UtcNow + DataTimeout;
            while (_chunks.Count == 0 && !_ended && _error is null)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !Monitor.Wait(_lock, left))
                {
                    _error = new TimeoutException("Нет данных от другого ПК");
                }
            }

            if (_chunks.Count == 0)
            {
                if (_error is not null)
                {
                    throw _error is IOException ? _error : new IOException(_error.Message, _error);
                }

                return 0;
            }

            copied = 0;
            while (copied < buffer.Length && _chunks.Count > 0)
            {
                var chunk = _chunks.Peek();
                var n = Math.Min(buffer.Length - copied, chunk.Length - _chunkOffset);
                chunk.AsSpan(_chunkOffset, n).CopyTo(buffer[copied..]);
                copied += n;
                _chunkOffset += n;
                if (_chunkOffset == chunk.Length)
                {
                    _chunks.Dequeue();
                    _chunkOffset = 0;
                }
            }

            _consumed += copied;
            if (!_ended && _consumed - _acked >= AckStepBytes)
            {
                _acked = _consumed;
                ackToSend = _consumed;
            }
        }

        if (ackToSend is { } bytes)
        {
            _owner.Send(new FileAck(RequestId, bytes));
        }

        return copied;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    internal void OnChunk(byte[] data)
    {
        lock (_lock)
        {
            if (_ended || _error is not null)
            {
                return;
            }

            _received += data.Length;
            if (_received > _size)
            {
                _error = new RemoteFileException(FileErrorCode.Changed);
            }
            else
            {
                _chunks.Enqueue(data);
            }

            Monitor.PulseAll(_lock);
        }
    }

    internal void OnEnd()
    {
        lock (_lock)
        {
            if (_received != _size)
            {
                _error ??= new RemoteFileException(FileErrorCode.Changed);
            }

            _ended = true;
            _sourceDone = true;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Ошибка от источника или обрыв соединения.</summary>
    internal void OnError(Exception error)
    {
        lock (_lock)
        {
            _sourceDone = true;
            _error ??= error;
            Monitor.PulseAll(_lock);
        }
    }

    protected override void Dispose(bool disposing)
    {
        bool cancel;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancel = _opened && !_sourceDone;
            _chunks.Clear();
            _error ??= new ObjectDisposedException(nameof(RemoteFileStream));
            Monitor.PulseAll(_lock);
        }

        if (cancel)
        {
            _owner.Send(new FileCancel(RequestId));
        }

        _owner.Remove(this);
        base.Dispose(disposing);
    }
}

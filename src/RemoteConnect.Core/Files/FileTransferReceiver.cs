using RemoteConnect.Core.Protocol;

namespace RemoteConnect.Core.Files;

/// <summary>
/// Сторона получателя: выдаёт потоки <see cref="RemoteFileStream"/> для файлов набора и раздаёт им
/// входящие <c>FileChunk</c>/<c>FileEnd</c>/<c>FileError</c> по номеру запроса.
/// При обрыве соединения <see cref="FailAll"/> сразу завершает все чтения ошибкой (FR-FCB-14).
/// </summary>
public sealed class FileTransferReceiver(Func<Message, bool> send, TimeSpan? dataTimeout = null)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<int, RemoteFileStream> _streams = [];
    private readonly TimeSpan _dataTimeout = dataTimeout ?? RemoteFileStream.DefaultDataTimeout;
    private int _nextRequestId;
    private Exception? _failure;

    public int OpenCount
    {
        get
        {
            lock (_lock)
            {
                return _streams.Count;
            }
        }
    }

    /// <summary>Поток для файла <paramref name="index"/> набора; данные запрашиваются при первом чтении.</summary>
    public RemoteFileStream Open(int offerId, int index, long size)
    {
        lock (_lock)
        {
            var stream = new RemoteFileStream(this, ++_nextRequestId, offerId, index, size, _dataTimeout);
            if (_failure is not null)
            {
                stream.OnError(_failure);
            }
            else
            {
                _streams.Add(stream.RequestId, stream);
            }

            return stream;
        }
    }

    /// <summary>Обрабатывает сообщения источника; возвращает false, если сообщение не относится к передаче файлов.</summary>
    public bool Handle(Message message)
    {
        switch (message)
        {
            case FileChunk chunk:
                Find(chunk.RequestId)?.OnChunk(chunk.Data);
                return true;
            case FileEnd end:
                Find(end.RequestId)?.OnEnd();
                return true;
            case FileError error:
                Find(error.RequestId)?.OnError(new RemoteFileException(error.Code));
                return true;
            default:
                return false;
        }
    }

    /// <summary>Соединение закрыто: все текущие и будущие чтения завершаются ошибкой.</summary>
    public void FailAll(string reason = "Соединение с другим ПК закрыто")
    {
        List<RemoteFileStream> streams;
        var error = new IOException(reason);
        lock (_lock)
        {
            _failure ??= error;
            streams = [.. _streams.Values];
            _streams.Clear();
        }

        foreach (var stream in streams)
        {
            stream.OnError(error);
        }
    }

    internal void Send(Message message) => send(message);

    internal void Remove(RemoteFileStream stream)
    {
        lock (_lock)
        {
            _streams.Remove(stream.RequestId);
        }
    }

    private RemoteFileStream? Find(int requestId)
    {
        lock (_lock)
        {
            return _streams.GetValueOrDefault(requestId);
        }
    }
}

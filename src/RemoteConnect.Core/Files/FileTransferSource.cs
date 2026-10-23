using RemoteConnect.Core.Protocol;

namespace RemoteConnect.Core.Files;

/// <summary>Идущая передача файла получателю (для уведомления FR-FCB-19).</summary>
public sealed record FileTransferProgress(string Name, long Sent, long Size);

/// <summary>
/// Сторона источника (spec-file-clipboard): хранит текущий набор и отдаёт получателю файлы из него по номеру.
/// <list type="bullet">
/// <item>Читать можно только <c>entries[index]</c> текущего набора — никаких путей в запросе (FR-FCB-16).</item>
/// <item>Новый набор или <see cref="Revoke"/> делают старый недоступным: <c>FileOpen</c> к нему → <see cref="FileErrorCode.OfferExpired"/> (FR-FCB-5).</item>
/// <item>Файл читается частями по 64 КБ; без <c>FileAck</c> в пути не больше <see cref="WindowBytes"/> (NFR-FCB-2).</item>
/// <item>Размер сверяется при открытии и в конце: изменился → <see cref="FileErrorCode.Changed"/> (FR-FCB-13).</item>
/// </list>
/// Части файла, <c>FileEnd</c> и <c>FileError</c> отправляются фоновой очередью соединения — в одном порядке
/// и без задержки трансляции экрана (FR-FCB-15).
/// </summary>
public sealed class FileTransferSource : IDisposable
{
    public const int WindowBytes = 1024 * 1024;
    public const int MaxConcurrentTransfers = 4;

    /// <summary>
    /// Окно заполнено, а подтверждений нет столько времени — получатель бросил чтение, не сообщив
    /// (программа без асинхронной вставки, сбой Проводника). Передача прекращается.
    /// </summary>
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(60);

    private readonly Func<Message, bool, bool> _send;
    private readonly Lock _lock = new();
    private readonly Dictionary<int, Transfer> _transfers = [];
    private OfferState? _offer;
    private int _nextOfferId;
    private bool _disposed;

    /// <param name="send">Отправка сообщения получателю: (сообщение, фоновая очередь).</param>
    public FileTransferSource(Func<Message, bool, bool> send)
    {
        _send = send;
    }

    /// <summary>Передача началась или закончилась (успешно, с ошибкой или отменой).</summary>
    public event Action? TransfersChanged;

    public int? CurrentOfferId
    {
        get
        {
            lock (_lock)
            {
                return _offer?.Id;
            }
        }
    }

    public IReadOnlyList<FileTransferProgress> ActiveTransfers
    {
        get
        {
            lock (_lock)
            {
                return [.. _transfers.Values.Select(t => new FileTransferProgress(Path.GetFileName(t.Entry.RelativePath), Interlocked.Read(ref t.Sent), t.Entry.Size))];
            }
        }
    }

    /// <summary>Делает набор текущим и отправляет его получателю. Идущие передачи старого набора не прерываются.</summary>
    public FileOffer Offer(FileOfferBuildResult set)
    {
        if (!set.IsOk)
        {
            throw new ArgumentException("Набор не построен", nameof(set));
        }

        FileOffer message;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var id = ++_nextOfferId;
            _offer = new OfferState(id, set.Entries, set.SourcePaths);
            message = new FileOffer(id, set.Entries);
        }

        _send(message, false);
        return message;
    }

    /// <summary>
    /// Отзывает набор: новые запросы получают <see cref="FileErrorCode.OfferExpired"/>.
    /// <paramref name="cancelTransfers"/> — прервать и идущие передачи (выключена синхронизация, конец сессии — FR-FCB-18);
    /// иначе начатая вставка доделывается (в буфер скопировано что-то новое).
    /// </summary>
    public void Revoke(bool cancelTransfers = true)
    {
        List<Transfer> cancelled;
        lock (_lock)
        {
            _offer = null;
            cancelled = cancelTransfers ? [.. _transfers.Values] : [];
        }

        foreach (var transfer in cancelled)
        {
            transfer.Cancel(notifyReceiver: true);
        }
    }

    /// <summary>Обрабатывает сообщения получателя; возвращает false, если сообщение не относится к файлам.</summary>
    public bool Handle(Message message)
    {
        switch (message)
        {
            case FileOpen open:
                Open(open);
                return true;
            case FileAck ack:
                Find(ack.RequestId)?.Acknowledge(ack.BytesConsumed);
                return true;
            case FileCancel cancel:
                Find(cancel.RequestId)?.Cancel(notifyReceiver: false);
                return true;
            default:
                return false;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }

        Revoke();
    }

    private Transfer? Find(int requestId)
    {
        lock (_lock)
        {
            return _transfers.GetValueOrDefault(requestId);
        }
    }

    private void Open(FileOpen request)
    {
        Transfer transfer;
        lock (_lock)
        {
            var error = Validate(request);
            if (error is { } code)
            {
                _send(new FileError(request.RequestId, code), true);
                return;
            }

            var offer = _offer!;
            transfer = new Transfer(this, request.RequestId, offer.Entries[request.Index], offer.SourcePaths[request.Index]);
            _transfers.Add(request.RequestId, transfer);
        }

        TransfersChanged?.Invoke();
        _ = Task.Run(transfer.RunAsync);
    }

    private FileErrorCode? Validate(FileOpen request)
    {
        if (_disposed || _offer is null || _offer.Id != request.OfferId)
        {
            return FileErrorCode.OfferExpired;
        }

        if (request.Index < 0 || request.Index >= _offer.Entries.Count || _offer.Entries[request.Index].IsDirectory)
        {
            return FileErrorCode.BadRequest;
        }

        if (_transfers.ContainsKey(request.RequestId) || _transfers.Count >= MaxConcurrentTransfers)
        {
            return FileErrorCode.BadRequest;
        }

        return null;
    }

    private void Finished(Transfer transfer)
    {
        lock (_lock)
        {
            _transfers.Remove(transfer.RequestId);
        }

        TransfersChanged?.Invoke();
    }

    private static FileErrorCode ErrorCode(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => FileErrorCode.NotFound,
        UnauthorizedAccessException => FileErrorCode.AccessDenied,

        // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION — файл заблокирован другой программой.
        IOException io when (io.HResult & 0xFFFF) is 32 or 33 => FileErrorCode.AccessDenied,
        _ => FileErrorCode.ReadError,
    };

    private sealed record OfferState(int Id, IReadOnlyList<FileOfferEntry> Entries, IReadOnlyList<string> SourcePaths);

    private sealed class FileChangedException() : IOException("Файл изменился после копирования");

    private sealed class Transfer(FileTransferSource owner, int requestId, FileOfferEntry entry, string path)
    {
        public long Sent;

        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _acked = new(0);
        private long _consumed;
        private volatile bool _notifyOnCancel;

        public int RequestId => requestId;

        public FileOfferEntry Entry => entry;

        public void Acknowledge(long consumed)
        {
            if (consumed > Interlocked.Read(ref _consumed))
            {
                Interlocked.Exchange(ref _consumed, consumed);
                _acked.Release();
            }
        }

        public void Cancel(bool notifyReceiver)
        {
            _notifyOnCancel = notifyReceiver;
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public async Task RunAsync()
        {
            var ct = _cts.Token;
            try
            {
                await using var file = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    BufferSize = 0,
                });

                if (file.Length != entry.Size)
                {
                    throw new FileChangedException();
                }

                var buffer = new byte[FileChunk.MaxSize];
                while (true)
                {
                    while (Interlocked.Read(ref Sent) - Interlocked.Read(ref _consumed) >= WindowBytes)
                    {
                        if (!await _acked.WaitAsync(AckTimeout, ct).ConfigureAwait(false))
                        {
                            owner._send(new FileError(requestId, FileErrorCode.ReadError), true);
                            return;
                        }
                    }

                    var read = await file.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (Interlocked.Read(ref Sent) + read > entry.Size)
                    {
                        throw new FileChangedException();
                    }

                    owner._send(new FileChunk(requestId, buffer[..read]), true);
                    Interlocked.Add(ref Sent, read);
                }

                if (Interlocked.Read(ref Sent) != entry.Size)
                {
                    throw new FileChangedException();
                }

                owner._send(new FileEnd(requestId), true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Отменил получатель (FileCancel) — сообщать нечего; набор отозван — получатель ещё ждёт данных.
                if (_notifyOnCancel)
                {
                    owner._send(new FileError(requestId, FileErrorCode.OfferExpired), true);
                }
            }
            catch (FileChangedException)
            {
                owner._send(new FileError(requestId, FileErrorCode.Changed), true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                owner._send(new FileError(requestId, ErrorCode(ex)), true);
            }
            finally
            {
                owner.Finished(this);
                _cts.Dispose();
            }
        }
    }
}

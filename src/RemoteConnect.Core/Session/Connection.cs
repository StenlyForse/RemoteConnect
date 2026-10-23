using System.Net;
using System.Threading.Channels;
using RemoteConnect.Core.Protocol;

namespace RemoteConnect.Core.Session;

/// <param name="Reason">Причина завершения.</param>
/// <param name="ByRemote">true — соединение закрыла удалённая сторона (или оно оборвалось).</param>
public sealed record DisconnectInfo(DisconnectReason Reason, bool ByRemote, Exception? Error = null);

public sealed class ConnectionClosedException(DisconnectInfo info)
    : Exception($"Соединение закрыто: {info.Reason}", info.Error)
{
    public DisconnectInfo Info { get; } = info;
}

/// <summary>
/// Двунаправленный канал сообщений поверх уже установленного (TLS) потока.
/// Отдельные циклы отправки, приёма и keep-alive; Ping/Pong/Bye обрабатываются внутри (NFR-7).
/// </summary>
public sealed class Connection : IAsyncDisposable
{
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultInactivityTimeout = TimeSpan.FromSeconds(10);

    private readonly Stream _stream;
    private readonly TimeProvider _time;
    private readonly TimeSpan _inactivityTimeout;
    private readonly Channel<Message> _outgoing = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<Message> _incoming = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<DisconnectInfo> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lastActivity;
    private int _closing;
    private Task _sendLoop = Task.CompletedTask;

    public Connection(Stream stream, EndPoint? remoteEndPoint, TimeProvider? time = null, TimeSpan? inactivityTimeout = null)
    {
        _stream = stream;
        RemoteEndPoint = remoteEndPoint;
        _time = time ?? TimeProvider.System;
        _inactivityTimeout = inactivityTimeout ?? DefaultInactivityTimeout;
    }

    public EndPoint? RemoteEndPoint { get; }

    public Task<DisconnectInfo> Completion => _closed.Task;

    public CancellationToken ClosedToken => _cts.Token;

    public bool IsOpen => !_closed.Task.IsCompleted;

    public void Start()
    {
        MarkActivity();
        _sendLoop = Task.Run(SendLoopAsync);
        _ = Task.Run(ReceiveLoopAsync);
        _ = Task.Run(KeepAliveLoopAsync);
    }

    /// <summary>Ставит сообщение в очередь отправки. Не блокирует.</summary>
    public bool Send(Message message) => _outgoing.Writer.TryWrite(message);

    public async ValueTask<Message> ReceiveAsync(CancellationToken ct = default)
    {
        try
        {
            return await _incoming.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new ConnectionClosedException(await Completion.ConfigureAwait(false));
        }
    }

    public async Task<T> ReceiveAsync<T>(TimeSpan timeout, CancellationToken ct = default) where T : Message
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        Message message;
        try
        {
            message = await ReceiveAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Не дождались {typeof(T).Name}");
        }

        return message as T ?? throw new ProtocolException($"Ожидалось {typeof(T).Name}, получено {message.Type}");
    }

    /// <summary>Отправляет Bye (если возможно) и закрывает соединение.</summary>
    public async Task CloseAsync(DisconnectReason reason)
    {
        if (Interlocked.Exchange(ref _closing, 1) == 1)
        {
            await Completion.ConfigureAwait(false);
            return;
        }

        if (!_closed.Task.IsCompleted)
        {
            _outgoing.Writer.TryWrite(new Bye(reason));
            _outgoing.Writer.TryComplete();
            await Task.WhenAny(_sendLoop, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        }

        Finish(new DisconnectInfo(reason, ByRemote: false));
    }

    public async ValueTask DisposeAsync()
    {
        if (IsOpen)
        {
            await CloseAsync(DisconnectReason.UserClosed).ConfigureAwait(false);
        }

        _cts.Dispose();
    }

    private void MarkActivity() => Interlocked.Exchange(ref _lastActivity, _time.GetTimestamp());

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var message in _outgoing.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await MessageFramer.WriteAsync(_stream, message, _cts.Token).ConfigureAwait(false);
                if (message is Bye)
                {
                    await _stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fail(ex);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var message = await MessageFramer.ReadAsync(_stream, MarkActivity, _cts.Token).ConfigureAwait(false);
                switch (message)
                {
                    case Ping ping:
                        Send(new Pong(ping.Timestamp));
                        break;
                    case Pong:
                        break;
                    case Bye bye:
                        Finish(new DisconnectInfo(bye.Reason, ByRemote: true));
                        return;
                    default:
                        _incoming.Writer.TryWrite(message);
                        break;
                }
            }
        }
        catch (ProtocolException ex)
        {
            Finish(new DisconnectInfo(DisconnectReason.ProtocolError, ByRemote: true, ex));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fail(ex);
        }
    }

    private async Task KeepAliveLoopAsync()
    {
        using var timer = new PeriodicTimer(PingInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                var idle = _time.GetElapsedTime(Interlocked.Read(ref _lastActivity));
                if (idle > _inactivityTimeout)
                {
                    Finish(new DisconnectInfo(DisconnectReason.Timeout, ByRemote: true));
                    return;
                }

                Send(new Ping(_time.GetTimestamp()));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Fail(Exception ex)
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }

        Finish(new DisconnectInfo(DisconnectReason.Error, ByRemote: true, ex));
    }

    private void Finish(DisconnectInfo info)
    {
        if (!_closed.TrySetResult(info))
        {
            return;
        }

        _incoming.Writer.TryComplete();
        _outgoing.Writer.TryComplete();
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _stream.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }
}

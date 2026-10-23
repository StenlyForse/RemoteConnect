using RemoteConnect.Core.Protocol;
using RemoteConnect.Core.Session;

namespace RemoteConnect.Core.Tests;

public class ConnectionQueueTests
{
    [Fact]
    public async Task NormalMessage_OvertakesQueuedBackgroundMessages()
    {
        var stream = new GatedStream();
        await using var connection = new Connection(stream, null);
        connection.Start();

        connection.Send(new FileChunk(1, [0]), background: true);
        await stream.FirstWriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
        connection.Send(new FileChunk(1, [1]), background: true);
        connection.Send(new FileChunk(1, [2]), background: true);
        connection.Send(new FrameAck(7));
        connection.Send(new FileChunk(1, [3]), background: true);
        stream.Release();

        var sent = await stream.ReadMessagesAsync(5, TimeSpan.FromSeconds(5));
        Assert.Equal(["chunk0", "frame7", "chunk1", "chunk2", "chunk3"], sent.Select(Describe));
    }

    [Fact]
    public async Task BackgroundMessages_AreSentWhenNormalQueueIsIdle()
    {
        var stream = new GatedStream();
        stream.Release();
        await using var connection = new Connection(stream, null);
        connection.Start();

        for (var i = 0; i < 3; i++)
        {
            connection.Send(new FileChunk(2, [(byte)i]), background: true);
        }

        var sent = await stream.ReadMessagesAsync(3, TimeSpan.FromSeconds(5));
        Assert.Equal(["chunk0", "chunk1", "chunk2"], sent.Select(Describe));
    }

    private static string Describe(Message message) => message switch
    {
        FileChunk c => $"chunk{c.Data[0]}",
        FrameAck f => $"frame{f.FrameNumber}",
        _ => message.Type.ToString(),
    };

    /// <summary>Поток, у которого записи ждут <see cref="Release"/>; чтение никогда не завершается.</summary>
    private sealed class GatedStream : Stream
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly MemoryStream _written = new();
        private readonly SemaphoreSlim _writtenSignal = new(0);

        public Task FirstWriteStarted => _firstWrite.Task;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void Release() => _gate.TrySetResult();

        /// <summary>Разбирает записанное и возвращает сообщения без Ping, когда их наберётся <paramref name="count"/>.</summary>
        public async Task<List<Message>> ReadMessagesAsync(int count, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                MemoryStream copy;
                lock (_written)
                {
                    copy = new MemoryStream(_written.ToArray());
                }

                var messages = new List<Message>();
                while (copy.Position < copy.Length)
                {
                    var message = await MessageFramer.ReadAsync(copy, null, default);
                    if (message is not Ping)
                    {
                        messages.Add(message);
                    }
                }

                if (messages.Count >= count)
                {
                    return messages;
                }

                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !await _writtenSignal.WaitAsync(left))
                {
                    throw new TimeoutException($"Отправлено {messages.Count} из {count}");
                }
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _firstWrite.TrySetResult();
            await _gate.Task.WaitAsync(cancellationToken);
            lock (_written)
            {
                _written.Write(buffer.Span);
            }

            _writtenSignal.Release();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

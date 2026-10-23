using System.Buffers;
using System.Buffers.Binary;

namespace RemoteConnect.Core.Protocol;

/// <summary>
/// Кадрирование сообщений в потоке: [тип: 1 байт][длина: 4 байта LE][полезная нагрузка].
/// </summary>
public static class MessageFramer
{
    public const int HeaderSize = 5;
    public const int MaxMessageSize = 16 * 1024 * 1024;
    private const int ReadChunkSize = 64 * 1024;

    public static byte[] Serialize(Message message)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((byte)message.Type);
            w.Write(0);
            MessageSerializer.Write(message, w);
        }

        var buffer = ms.ToArray();
        var length = buffer.Length - HeaderSize;
        if (length > MaxMessageSize)
        {
            throw new ProtocolException($"Сообщение слишком большое: {length} байт");
        }

        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(1, 4), length);
        return buffer;
    }

    public static async Task WriteAsync(Stream stream, Message message, CancellationToken ct)
    {
        var buffer = Serialize(message);
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
    }

    /// <summary>Читает одно сообщение. <paramref name="onActivity"/> вызывается при получении каждой порции данных.</summary>
    public static async Task<Message> ReadAsync(Stream stream, Action? onActivity, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        onActivity?.Invoke();

        var type = (MessageType)header[0];
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1, 4));
        if (length is < 0 or > MaxMessageSize)
        {
            throw new ProtocolException($"Недопустимая длина сообщения: {length}");
        }

        var payload = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
        try
        {
            var read = 0;
            while (read < length)
            {
                var chunk = Math.Min(ReadChunkSize, length - read);
                await stream.ReadExactlyAsync(payload.AsMemory(read, chunk), ct).ConfigureAwait(false);
                read += chunk;
                onActivity?.Invoke();
            }

            using var ms = new MemoryStream(payload, 0, length, writable: false);
            using var r = new BinaryReader(ms);
            var message = MessageSerializer.Read(type, r);
            if (ms.Position != length)
            {
                throw new ProtocolException($"Лишние данные в сообщении {type}");
            }

            return message;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
    }
}

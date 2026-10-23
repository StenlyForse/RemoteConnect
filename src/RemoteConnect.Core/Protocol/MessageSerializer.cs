using System.Text;

namespace RemoteConnect.Core.Protocol;

public sealed class ProtocolException(string message) : Exception(message);

/// <summary>Ручная бинарная сериализация сообщений (без рефлексии).</summary>
public static class MessageSerializer
{
    private const int MaxStringBytes = 1024;
    private const int MaxBlobBytes = MessageFramer.MaxMessageSize;
    private const int MaxListCount = 4096;

    /// <summary>Лимит набора файлов — 10 000 элементов (FR-FCB-3).</summary>
    public const int MaxFileOfferEntries = 10_000;

    public static void Write(Message message, BinaryWriter w)
    {
        switch (message)
        {
            case Hello m:
                w.Write(m.ProtocolVersion);
                WriteString(w, m.Name);
                w.Write((byte)m.ClientRole);
                break;
            case AuthChallenge m:
                WriteBlob(w, m.Salt);
                w.Write(m.Iterations);
                WriteBlob(w, m.Nonce);
                break;
            case AuthResponse m:
                WriteBlob(w, m.Proof);
                break;
            case AuthResult m:
                w.Write((byte)m.Status);
                w.Write(m.AttemptsLeft);
                w.Write(m.BlockedSeconds);
                WriteString(w, m.HostName);
                break;
            case ConsentResult m:
                w.Write((byte)m.Decision);
                break;
            case ScreenInfo m:
                w.Write(m.Monitors.Count);
                foreach (var mon in m.Monitors)
                {
                    WriteString(w, mon.Name);
                    w.Write(mon.Width);
                    w.Write(mon.Height);
                    w.Write(mon.IsPrimary);
                }
                w.Write(m.ActiveMonitor);
                w.Write(m.Width);
                w.Write(m.Height);
                break;
            case FrameTiles m:
                w.Write(m.FrameNumber);
                w.Write(m.Tiles.Count);
                foreach (var t in m.Tiles)
                {
                    w.Write(t.X);
                    w.Write(t.Y);
                    w.Write(t.Width);
                    w.Write(t.Height);
                    WriteBlob(w, t.Data);
                }
                break;
            case FrameAck m:
                w.Write(m.FrameNumber);
                break;
            case MouseInput m:
                w.Write((byte)m.Action);
                w.Write(m.X);
                w.Write(m.Y);
                w.Write((byte)m.Button);
                w.Write(m.WheelDelta);
                break;
            case KeyInput m:
                w.Write(m.VirtualKey);
                w.Write(m.ScanCode);
                w.Write(m.IsDown);
                w.Write(m.IsExtended);
                break;
            case ReleaseAll:
                break;
            case SelectMonitor m:
                w.Write(m.Index);
                break;
            case InputEnabled m:
                w.Write(m.Enabled);
                break;
            case Ping m:
                w.Write(m.Timestamp);
                break;
            case Pong m:
                w.Write(m.Timestamp);
                break;
            case Bye m:
                w.Write((byte)m.Reason);
                break;
            case ClipboardData m:
                w.Write((byte)m.Format);
                WriteBlob(w, m.Data);
                break;
            case ClipboardEnabled m:
                w.Write(m.Enabled);
                break;
            case FileOffer m:
                w.Write(m.OfferId);
                w.Write(m.Entries.Count);
                foreach (var e in m.Entries)
                {
                    WriteString(w, e.RelativePath);
                    w.Write(e.IsDirectory);
                    w.Write(e.Size);
                    w.Write(e.LastWriteUtc.Ticks);
                }
                break;
            case FileOpen m:
                w.Write(m.RequestId);
                w.Write(m.OfferId);
                w.Write(m.Index);
                break;
            case FileChunk m:
                w.Write(m.RequestId);
                WriteBlob(w, m.Data);
                break;
            case FileAck m:
                w.Write(m.RequestId);
                w.Write(m.BytesConsumed);
                break;
            case FileEnd m:
                w.Write(m.RequestId);
                break;
            case FileError m:
                w.Write(m.RequestId);
                w.Write((byte)m.Code);
                break;
            case FileCancel m:
                w.Write(m.RequestId);
                break;
            default:
                throw new ArgumentException($"Неизвестное сообщение {message.GetType().Name}", nameof(message));
        }
    }

    public static Message Read(MessageType type, BinaryReader r)
    {
        try
        {
            return type switch
            {
                MessageType.Hello => new Hello(r.ReadInt32(), ReadString(r), ReadEnum<SessionRole>(r)),
                MessageType.AuthChallenge => new AuthChallenge(ReadBlob(r), r.ReadInt32(), ReadBlob(r)),
                MessageType.AuthResponse => new AuthResponse(ReadBlob(r)),
                MessageType.AuthResult => new AuthResult(ReadEnum<AuthStatus>(r), r.ReadInt32(), r.ReadInt32(), ReadString(r)),
                MessageType.ConsentResult => new ConsentResult(ReadEnum<ConsentDecision>(r)),
                MessageType.ScreenInfo => ReadScreenInfo(r),
                MessageType.FrameTiles => ReadFrameTiles(r),
                MessageType.FrameAck => new FrameAck(r.ReadInt32()),
                MessageType.MouseInput => new MouseInput(ReadEnum<MouseAction>(r), r.ReadUInt16(), r.ReadUInt16(), ReadEnum<MouseButton>(r), r.ReadInt16()),
                MessageType.KeyInput => new KeyInput(r.ReadUInt16(), r.ReadUInt16(), r.ReadBoolean(), r.ReadBoolean()),
                MessageType.ReleaseAll => new ReleaseAll(),
                MessageType.SelectMonitor => new SelectMonitor(r.ReadInt32()),
                MessageType.InputEnabled => new InputEnabled(r.ReadBoolean()),
                MessageType.Ping => new Ping(r.ReadInt64()),
                MessageType.Pong => new Pong(r.ReadInt64()),
                MessageType.Bye => new Bye(ReadEnum<DisconnectReason>(r)),
                MessageType.ClipboardData => new ClipboardData(ReadEnum<ClipboardFormat>(r), ReadBlob(r)),
                MessageType.ClipboardEnabled => new ClipboardEnabled(r.ReadBoolean()),
                MessageType.FileOffer => ReadFileOffer(r),
                MessageType.FileOpen => new FileOpen(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()),
                MessageType.FileChunk => ReadFileChunk(r),
                MessageType.FileAck => new FileAck(r.ReadInt32(), r.ReadInt64()),
                MessageType.FileEnd => new FileEnd(r.ReadInt32()),
                MessageType.FileError => new FileError(r.ReadInt32(), ReadEnum<FileErrorCode>(r)),
                MessageType.FileCancel => new FileCancel(r.ReadInt32()),
                _ => throw new ProtocolException($"Неизвестный тип сообщения {(byte)type}"),
            };
        }
        catch (EndOfStreamException)
        {
            throw new ProtocolException($"Сообщение {type} обрезано");
        }
    }

    private static ScreenInfo ReadScreenInfo(BinaryReader r)
    {
        var count = ReadCount(r);
        var monitors = new List<MonitorDescriptor>(count);
        for (var i = 0; i < count; i++)
        {
            monitors.Add(new MonitorDescriptor(ReadString(r), r.ReadInt32(), r.ReadInt32(), r.ReadBoolean()));
        }

        return new ScreenInfo(monitors, r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
    }

    private static FrameTiles ReadFrameTiles(BinaryReader r)
    {
        var frame = r.ReadInt32();
        var count = ReadCount(r);
        var tiles = new List<EncodedTile>(count);
        for (var i = 0; i < count; i++)
        {
            tiles.Add(new EncodedTile(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), ReadBlob(r)));
        }

        return new FrameTiles(frame, tiles);
    }

    private static FileOffer ReadFileOffer(BinaryReader r)
    {
        var offerId = r.ReadInt32();
        var count = r.ReadInt32();
        if (count is < 0 or > MaxFileOfferEntries)
        {
            throw new ProtocolException($"Недопустимое число файлов: {count}");
        }

        var entries = new List<FileOfferEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var path = ReadString(r);
            var isDirectory = r.ReadBoolean();
            var size = r.ReadInt64();
            var ticks = r.ReadInt64();
            if (size < 0 || (isDirectory && size != 0) || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            {
                throw new ProtocolException($"Недопустимый элемент набора файлов: {path}");
            }

            entries.Add(new FileOfferEntry(path, isDirectory, size, new DateTime(ticks, DateTimeKind.Utc)));
        }

        return new FileOffer(offerId, entries);
    }

    private static FileChunk ReadFileChunk(BinaryReader r)
    {
        var requestId = r.ReadInt32();
        var data = ReadBlob(r);
        if (data.Length > FileChunk.MaxSize)
        {
            throw new ProtocolException($"Слишком большая часть файла: {data.Length}");
        }

        return new FileChunk(requestId, data);
    }

    private static TEnum ReadEnum<TEnum>(BinaryReader r) where TEnum : struct, Enum
    {
        var value = r.ReadByte();
        var result = (TEnum)Enum.ToObject(typeof(TEnum), value);
        if (!Enum.IsDefined(result))
        {
            throw new ProtocolException($"Недопустимое значение {typeof(TEnum).Name}: {value}");
        }

        return result;
    }

    private static int ReadCount(BinaryReader r)
    {
        var count = r.ReadInt32();
        if (count is < 0 or > MaxListCount)
        {
            throw new ProtocolException($"Недопустимое число элементов: {count}");
        }

        return count;
    }

    private static void WriteString(BinaryWriter w, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaxStringBytes)
        {
            throw new ArgumentException("Строка слишком длинная", nameof(value));
        }

        WriteBlob(w, bytes);
    }

    private static string ReadString(BinaryReader r)
    {
        var length = r.ReadInt32();
        if (length is < 0 or > MaxStringBytes)
        {
            throw new ProtocolException($"Недопустимая длина строки: {length}");
        }

        return Encoding.UTF8.GetString(r.ReadBytes(length));
    }

    private static void WriteBlob(BinaryWriter w, byte[] data)
    {
        w.Write(data.Length);
        w.Write(data);
    }

    private static byte[] ReadBlob(BinaryReader r)
    {
        var length = r.ReadInt32();
        if (length is < 0 or > MaxBlobBytes)
        {
            throw new ProtocolException($"Недопустимая длина данных: {length}");
        }

        var data = r.ReadBytes(length);
        if (data.Length != length)
        {
            throw new EndOfStreamException();
        }

        return data;
    }
}

using RemoteConnect.Core.Protocol;

namespace RemoteConnect.Core.Tests;

public class ProtocolTests
{
    public static TheoryData<Message> SimpleMessages() =>
    [
        new Hello(1, "Алекс", SessionRole.Controlled),
        new AuthResult(AuthStatus.WrongPassword, 3, 0, "host"),
        new ConsentResult(ConsentDecision.TimedOut),
        new FrameAck(42),
        new MouseInput(MouseAction.Wheel, 100, 65535, MouseButton.None, -120),
        new KeyInput(0x5B, 0x5B, true, true),
        new ReleaseAll(),
        new SelectMonitor(2),
        new InputEnabled(false),
        new Ping(123456789),
        new Pong(-1),
        new Bye(DisconnectReason.ConsentDenied),
        new ClipboardEnabled(false),
        new FileOpen(5, 77, 9999),
        new FileAck(5, 5_000_000_000),
        new FileEnd(5),
        new FileError(5, FileErrorCode.Changed),
        new FileCancel(-3),
        new AppVersion("0.4.0"),
        new ChatMessage("Привет!\nСмотри https://example.com/a?b=1"),
        new StreamMode(StreamModeKind.Faster),
    ];

    [Theory]
    [MemberData(nameof(SimpleMessages))]
    public async Task SimpleMessages_RoundTrip(Message message)
    {
        var result = await RoundTrip(message);
        Assert.Equal(message, result);
    }

    [Fact]
    public async Task AuthChallenge_RoundTrip()
    {
        var message = new AuthChallenge([1, 2, 3], 600_000, [9, 8, 7, 6]);
        var result = Assert.IsType<AuthChallenge>(await RoundTrip(message));
        Assert.Equal(message.Salt, result.Salt);
        Assert.Equal(message.Iterations, result.Iterations);
        Assert.Equal(message.Nonce, result.Nonce);
    }

    [Fact]
    public async Task ScreenInfo_RoundTrip()
    {
        var message = new ScreenInfo([new MonitorDescriptor("DISPLAY1", 1920, 1080, true), new MonitorDescriptor("DISPLAY2", 1280, 1024, false)], 1, 1280, 1024);
        var result = Assert.IsType<ScreenInfo>(await RoundTrip(message));
        Assert.Equal(message.Monitors, result.Monitors);
        Assert.Equal((1, 1280, 1024), (result.ActiveMonitor, result.Width, result.Height));
    }

    [Fact]
    public async Task FrameTiles_RoundTrip()
    {
        var message = new FrameTiles(7, 55, false, [new EncodedTile(0, 64, 128, 64, [0xFF, 0xD8]), new EncodedTile(64, 0, 64, 64, [])]);
        var result = Assert.IsType<FrameTiles>(await RoundTrip(message));
        Assert.Equal((7, (byte)55, false), (result.FrameNumber, result.Quality, result.EndOfFrame));
        Assert.Equal(2, result.Tiles.Count);
        Assert.Equal(message.Tiles[0].Data, result.Tiles[0].Data);
        Assert.Equal((64, 0, 64, 64), (result.Tiles[1].X, result.Tiles[1].Y, result.Tiles[1].Width, result.Tiles[1].Height));
    }

    [Fact]
    public async Task ClipboardData_RoundTrip()
    {
        var message = new ClipboardData(ClipboardFormat.Image, [0x89, 0x50, 0x4E, 0x47]);
        var result = Assert.IsType<ClipboardData>(await RoundTrip(message));
        Assert.Equal(ClipboardFormat.Image, result.Format);
        Assert.Equal(message.Data, result.Data);
    }

    [Fact]
    public async Task FileOffer_RoundTrip()
    {
        var date = new DateTime(2026, 10, 23, 12, 30, 15, 123, DateTimeKind.Utc);
        var message = new FileOffer(
            42,
            [
                new FileOfferEntry("Отчёты", true, 0, date),
                new FileOfferEntry(@"Отчёты\пустая", true, 0, date),
                new FileOfferEntry(@"Отчёты\итоги 2026.xlsx", false, 5_000_000_000, date.AddDays(-1)),
            ]);
        var result = Assert.IsType<FileOffer>(await RoundTrip(message));
        Assert.Equal(42, result.OfferId);
        Assert.Equal(message.Entries, result.Entries);
        Assert.Equal(DateTimeKind.Utc, result.Entries[2].LastWriteUtc.Kind);
    }

    [Fact]
    public async Task FileOffer_TenThousandEntries_RoundTrip()
    {
        var entries = Enumerable.Range(0, 10_000).Select(i => new FileOfferEntry($"файл{i}.txt", false, i, DateTime.UnixEpoch)).ToList();
        var result = Assert.IsType<FileOffer>(await RoundTrip(new FileOffer(1, entries)));
        Assert.Equal(10_000, result.Entries.Count);
    }

    [Fact]
    public async Task FileOffer_RejectsTooManyEntries()
    {
        var bytes = MessageFramer.Serialize(new FileOffer(1, []));
        BitConverter.TryWriteBytes(bytes.AsSpan(MessageFramer.HeaderSize + 4, 4), 10_001);
        await Assert.ThrowsAsync<ProtocolException>(() => MessageFramer.ReadAsync(new MemoryStream(bytes), null, default));
    }

    [Fact]
    public async Task FileOffer_RejectsDirectoryWithSize()
    {
        var bytes = MessageFramer.Serialize(new FileOffer(1, [new FileOfferEntry("a", true, 5, DateTime.UnixEpoch)]));
        await Assert.ThrowsAsync<ProtocolException>(() => MessageFramer.ReadAsync(new MemoryStream(bytes), null, default));
    }

    [Fact]
    public async Task FileChunk_RoundTrip()
    {
        var data = Enumerable.Range(0, FileChunk.MaxSize).Select(i => (byte)i).ToArray();
        var result = Assert.IsType<FileChunk>(await RoundTrip(new FileChunk(9, data)));
        Assert.Equal(9, result.RequestId);
        Assert.Equal(data, result.Data);
    }

    [Fact]
    public async Task FileChunk_RejectsOversized()
    {
        var bytes = MessageFramer.Serialize(new FileChunk(9, new byte[FileChunk.MaxSize + 1]));
        await Assert.ThrowsAsync<ProtocolException>(() => MessageFramer.ReadAsync(new MemoryStream(bytes), null, default));
    }

    [Fact]
    public async Task Read_RejectsOversizedLength()
    {
        var bytes = new byte[] { (byte)MessageType.Ping, 0xFF, 0xFF, 0xFF, 0x7F };
        await Assert.ThrowsAsync<ProtocolException>(() => MessageFramer.ReadAsync(new MemoryStream(bytes), null, default));
    }

    [Fact]
    public async Task Read_RejectsUnknownType()
    {
        var bytes = new byte[] { 200, 0, 0, 0, 0 };
        await Assert.ThrowsAsync<ProtocolException>(() => MessageFramer.ReadAsync(new MemoryStream(bytes), null, default));
    }

    [Fact]
    public async Task Read_RejectsInvalidEnum()
    {
        var bytes = MessageFramer.Serialize(new Bye(DisconnectReason.UserClosed));
        bytes[^1] = 250;
        await Assert.ThrowsAsync<ProtocolException>(() => MessageFramer.ReadAsync(new MemoryStream(bytes), null, default));
    }

    [Fact]
    public async Task Read_RejectsTrailingBytes()
    {
        var bytes = MessageFramer.Serialize(new FrameAck(1)).Concat(new byte[] { 0 }).ToArray();
        bytes[1] = 5;
        await Assert.ThrowsAsync<ProtocolException>(() => MessageFramer.ReadAsync(new MemoryStream(bytes), null, default));
    }

    [Fact]
    public async Task ChatMessage_MaxLength_RoundTrips()
    {
        var text = string.Concat(Enumerable.Repeat("Ж", ChatMessage.MaxLength - 1)) + "\n";
        var result = Assert.IsType<ChatMessage>(await RoundTrip(new ChatMessage(text)));
        Assert.Equal(text, result.Text);
    }

    [Fact]
    public void ChatMessage_TooLong_IsNotSent() =>
        Assert.Throws<ArgumentException>(() => MessageFramer.Serialize(new ChatMessage(new string('a', ChatMessage.MaxLength + 1))));

    [Fact]
    public async Task ChatMessage_RejectsMoreCharsThanLimit()
    {
        var bytes = TextFrame(MessageType.ChatMessage, new string('a', ChatMessage.MaxLength + 1));
        await Assert.ThrowsAsync<ProtocolException>(() => MessageFramer.ReadAsync(new MemoryStream(bytes), null, default));
    }

    [Fact]
    public async Task ChatMessage_RejectsMoreBytesThanLimit()
    {
        // Больше 16 КБ — отклоняется по длине, до чтения и декодирования строки.
        var bytes = TextFrame(MessageType.ChatMessage, new string('a', ChatMessage.MaxBytes + 1));
        await Assert.ThrowsAsync<ProtocolException>(() => MessageFramer.ReadAsync(new MemoryStream(bytes), null, default));
    }

    [Fact]
    public async Task StreamMode_RejectsUnknownMode()
    {
        var bytes = MessageFramer.Serialize(new StreamMode(StreamModeKind.Auto));
        bytes[^1] = 9;
        await Assert.ThrowsAsync<ProtocolException>(() => MessageFramer.ReadAsync(new MemoryStream(bytes), null, default));
    }

    [Fact]
    public async Task AppVersion_RejectsTooLong()
    {
        var bytes = TextFrame(MessageType.AppVersion, new string('9', AppVersion.MaxLength + 1));
        await Assert.ThrowsAsync<ProtocolException>(() => MessageFramer.ReadAsync(new MemoryStream(bytes), null, default));
    }

    /// <summary>Кадр с одной строкой без проверок отправителя — как его мог бы прислать чужой клиент.</summary>
    private static byte[] TextFrame(MessageType type, string text)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(text);
        var frame = new byte[MessageFramer.HeaderSize + 4 + data.Length];
        frame[0] = (byte)type;
        BitConverter.TryWriteBytes(frame.AsSpan(1, 4), 4 + data.Length);
        BitConverter.TryWriteBytes(frame.AsSpan(MessageFramer.HeaderSize, 4), data.Length);
        data.CopyTo(frame, MessageFramer.HeaderSize + 4);
        return frame;
    }

    private static async Task<Message> RoundTrip(Message message)
    {
        var stream = new MemoryStream();
        await MessageFramer.WriteAsync(stream, message, default);
        stream.Position = 0;
        return await MessageFramer.ReadAsync(stream, null, default);
    }
}

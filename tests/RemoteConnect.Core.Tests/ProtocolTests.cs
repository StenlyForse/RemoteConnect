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
        var message = new FrameTiles(7, [new EncodedTile(0, 64, 128, 64, [0xFF, 0xD8]), new EncodedTile(64, 0, 64, 64, [])]);
        var result = Assert.IsType<FrameTiles>(await RoundTrip(message));
        Assert.Equal(7, result.FrameNumber);
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

    private static async Task<Message> RoundTrip(Message message)
    {
        var stream = new MemoryStream();
        await MessageFramer.WriteAsync(stream, message, default);
        stream.Position = 0;
        return await MessageFramer.ReadAsync(stream, null, default);
    }
}

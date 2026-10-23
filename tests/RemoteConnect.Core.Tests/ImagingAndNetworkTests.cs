using System.Net;
using Microsoft.Extensions.Time.Testing;
using RemoteConnect.Core.Imaging;
using RemoteConnect.Core.Network;
using RemoteConnect.Core.Session;

namespace RemoteConnect.Core.Tests;

public class TileDifferTests
{
    private const int Width = 200;
    private const int Height = 150;
    private const int Stride = Width * 4;

    [Fact]
    public void IdenticalFrames_NoDirtyRects()
    {
        var a = new byte[Stride * Height];
        Assert.Empty(TileDiffer.FindDirtyRects(a, a.ToArray(), Width, Height, Stride));
    }

    [Fact]
    public void SinglePixelChange_MarksOneTile()
    {
        var a = new byte[Stride * Height];
        var b = a.ToArray();
        SetPixel(b, 70, 10);
        var rects = TileDiffer.FindDirtyRects(a, b, Width, Height, Stride);
        Assert.Equal([new PixelRect(64, 0, 64, 64)], rects);
    }

    [Fact]
    public void EdgeTiles_AreClippedToFrame()
    {
        var a = new byte[Stride * Height];
        var b = a.ToArray();
        SetPixel(b, 199, 149);
        Assert.Equal([new PixelRect(192, 128, 8, 22)], TileDiffer.FindDirtyRects(a, b, Width, Height, Stride));
    }

    [Fact]
    public void AdjacentTilesInRow_MergeIntoRun()
    {
        var a = new byte[Stride * Height];
        var b = a.ToArray();
        SetPixel(b, 10, 10);
        SetPixel(b, 100, 10);
        Assert.Equal([new PixelRect(0, 0, 128, 64)], TileDiffer.FindDirtyRects(a, b, Width, Height, Stride));
    }

    [Fact]
    public void FullChange_MergesIntoSingleRect()
    {
        var a = new byte[Stride * Height];
        var b = Enumerable.Repeat((byte)1, Stride * Height).ToArray();
        Assert.Equal([TileDiffer.Full(Width, Height)], TileDiffer.FindDirtyRects(a, b, Width, Height, Stride));
    }

    [Fact]
    public void SeparateAreas_StaySeparate()
    {
        var a = new byte[Stride * Height];
        var b = a.ToArray();
        SetPixel(b, 5, 5);
        SetPixel(b, 150, 140);
        var rects = TileDiffer.FindDirtyRects(a, b, Width, Height, Stride);
        Assert.Equal(2, rects.Count);
        Assert.Contains(new PixelRect(0, 0, 64, 64), rects);
        Assert.Contains(new PixelRect(128, 128, 64, 22), rects);
    }

    private static void SetPixel(byte[] frame, int x, int y) => frame[(y * Stride) + (x * 4) + 1] = 0xFF;
}

public class CoordinateMapperTests
{
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(800, 450, 65535, 65535)]
    [InlineData(400, 225, 32768, 32768)]
    [InlineData(-10, 900, 0, 65535)]
    public void ToNormalized_ScalesAndClamps(double x, double y, int nx, int ny)
    {
        Assert.Equal(((ushort)nx, (ushort)ny), CoordinateMapper.ToNormalized(x, y, 800, 450));
    }

    [Fact]
    public void ToPixel_MapsToMonitorWithOffset()
    {
        Assert.Equal((-1920, 0), CoordinateMapper.ToPixel(0, 0, -1920, 0, 1920, 1080));
        Assert.Equal((-1, 1079), CoordinateMapper.ToPixel(65535, 65535, -1920, 0, 1920, 1080));
    }

    [Fact]
    public void RoundTrip_PreservesPixelOnDifferentScale()
    {
        var (nx, ny) = CoordinateMapper.ToNormalized(480, 270, 960, 540);
        Assert.Equal((960, 540), CoordinateMapper.ToPixel(nx, ny, 0, 0, 1920, 1080));
    }
}

public class NetworkTests
{
    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    public void IsPrivateOrShared(string address, bool expected)
    {
        Assert.Equal(expected, NatClassifier.IsPrivateOrShared(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("203.0.113.5", NatVerdict.PublicAddress)]
    [InlineData("100.70.1.2", NatVerdict.Cgnat)]
    [InlineData("192.168.1.1", NatVerdict.Cgnat)]
    [InlineData(null, NatVerdict.Unknown)]
    public void Classify_ByRouterAddress(string? router, NatVerdict expected)
    {
        Assert.Equal(expected, NatClassifier.Classify(router is null ? null : IPAddress.Parse(router)));
    }

    [Theory]
    [InlineData("198.51.100.20", "203.0.113.45", "198.51.100.20")] // VPN: внешний сервис видит адрес VPN
    [InlineData("100.70.1.2", "203.0.113.5", "203.0.113.5")]
    [InlineData(null, "203.0.113.5", "203.0.113.5")]
    [InlineData(null, null, null)]
    public void ChooseConnectionAddress_PrefersRouterPublicIp(string? router, string? publicIp, string? expected)
    {
        var chosen = NatClassifier.ChooseConnectionAddress(router is null ? null : IPAddress.Parse(router), publicIp is null ? null : IPAddress.Parse(publicIp));
        Assert.Equal(expected, chosen?.ToString());
    }

    [Theory]
    [InlineData("1.2.3.4", "1.2.3.4", 47800)]
    [InlineData("1.2.3.4:5000", "1.2.3.4", 5000)]
    [InlineData(" example.com:123 ", "example.com", 123)]
    [InlineData("[2001:db8::1]:47801", "2001:db8::1", 47801)]
    [InlineData("2001:db8::1", "2001:db8::1", 47800)]
    public void Parse_Address(string text, string host, int port)
    {
        Assert.True(ConnectionInfoParser.TryParse(text, out var target));
        Assert.Equal((host, port, (string?)null), (target.Host, target.Port, target.Password));
    }

    [Fact]
    public void Parse_CopiedHostText_ExtractsPassword()
    {
        var text = ConnectionInfoParser.Format("203.0.113.5", 47800, "k7m-29x");
        Assert.True(ConnectionInfoParser.TryParse(text, out var target));
        Assert.Equal(("203.0.113.5", 47800, "k7m-29x"), (target.Host, target.Port, target.Password));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2.3.4:0")]
    [InlineData("1.2.3.4:99999")]
    [InlineData("host:abc")]
    [InlineData("[::1")]
    public void Parse_Invalid(string text)
    {
        Assert.False(ConnectionInfoParser.TryParse(text, out _));
    }
}

public class AutoStopTimerTests
{
    [Fact]
    public void Fires_AfterTimeout()
    {
        var time = new FakeTimeProvider();
        var fired = 0;
        using var timer = new AutoStopTimer(time, TimeSpan.FromMinutes(30), () => fired++);
        timer.Restart();

        time.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(0, fired);
        Assert.Equal(TimeSpan.FromMinutes(1), timer.Remaining);

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, fired);
        Assert.Null(timer.Remaining);
    }

    [Fact]
    public void Pause_PreventsFiring_RestartBeginsFullInterval()
    {
        var time = new FakeTimeProvider();
        var fired = 0;
        using var timer = new AutoStopTimer(time, TimeSpan.FromMinutes(30), () => fired++);
        timer.Restart();
        time.Advance(TimeSpan.FromMinutes(20));
        timer.Pause();
        time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(0, fired);

        timer.Restart();
        time.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(0, fired);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void ZeroTimeout_Disabled()
    {
        var time = new FakeTimeProvider();
        var fired = 0;
        using var timer = new AutoStopTimer(time, TimeSpan.Zero, () => fired++);
        timer.Restart();
        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, fired);
        Assert.Null(timer.Remaining);
    }
}

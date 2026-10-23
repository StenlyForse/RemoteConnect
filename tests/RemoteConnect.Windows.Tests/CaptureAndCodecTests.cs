using System.Diagnostics;
using RemoteConnect.Core.Imaging;
using RemoteConnect.Windows.Capture;
using RemoteConnect.Windows.Encoding;
using Xunit.Abstractions;

namespace RemoteConnect.Windows.Tests;

public class CaptureAndCodecTests(ITestOutputHelper output)
{
    [Fact]
    public void Jpeg_RoundTrip_PreservesImageApproximately()
    {
        const int width = 128;
        const int height = 64;
        const int stride = width * 4;
        var frame = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * stride) + (x * 4);
                frame[i] = (byte)(x * 2);
                frame[i + 1] = (byte)(y * 4);
                frame[i + 2] = 128;
                frame[i + 3] = 255;
            }
        }

        var rect = new PixelRect(64, 0, 64, 64);
        var jpeg = JpegTileCodec.Encode(frame, stride, rect, quality: 90);
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);

        var decoded = new byte[64 * 64 * 4];
        JpegTileCodec.Decode(jpeg, decoded, 64, 64);

        var maxDiff = 0;
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var original = frame[(y * stride) + ((x + 64) * 4) + c];
                    var restored = decoded[(y * 64 * 4) + (x * 4) + c];
                    maxDiff = Math.Max(maxDiff, Math.Abs(original - restored));
                }
            }
        }

        Assert.True(maxDiff < 24, $"Слишком большое расхождение после JPEG: {maxDiff}");
    }

    [Fact]
    public void Decode_RejectsSizeMismatch()
    {
        var frame = new byte[64 * 64 * 4];
        var jpeg = JpegTileCodec.Encode(frame, 64 * 4, new PixelRect(0, 0, 64, 64));
        Assert.Throws<InvalidDataException>(() => JpegTileCodec.Decode(jpeg, new byte[128 * 64 * 4], 128, 64));
    }

    [Fact]
    public void Capture_PrimaryMonitor_AndMeasure()
    {
        var monitors = DisplayMonitors.GetAll();
        Assert.NotEmpty(monitors);
        Assert.True(monitors[0].IsPrimary);

        var monitor = monitors[0];
        var stride = GdiScreenCapturer.StrideFor(monitor.Width);
        var buffer = new byte[stride * monitor.Height];
        using var capturer = new GdiScreenCapturer();

        var sw = Stopwatch.StartNew();
        const int frames = 10;
        for (var i = 0; i < frames; i++)
        {
            capturer.Capture(monitor, buffer);
        }

        var captureMs = sw.Elapsed.TotalMilliseconds / frames;
        sw.Restart();
        var jpeg = JpegTileCodec.Encode(buffer, stride, TileDiffer.Full(monitor.Width, monitor.Height));
        var encodeMs = sw.Elapsed.TotalMilliseconds;

        output.WriteLine($"{monitor.Width}x{monitor.Height}: захват {captureMs:F1} мс/кадр, JPEG полного кадра {encodeMs:F1} мс, {jpeg.Length / 1024} КБ");
        Assert.Contains(buffer, b => b != 0);
    }
}

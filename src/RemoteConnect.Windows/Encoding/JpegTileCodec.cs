using RemoteConnect.Core.Imaging;
using SkiaSharp;

namespace RemoteConnect.Windows.Encoding;

/// <summary>JPEG-кодирование областей кадра BGRA и обратное декодирование (SkiaSharp / libjpeg-turbo).</summary>
public static class JpegTileCodec
{
    public const int DefaultQuality = 70;

    public static unsafe byte[] Encode(byte[] frame, int stride, PixelRect rect, int quality = DefaultQuality)
    {
        var info = new SKImageInfo(rect.Width, rect.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        fixed (byte* p = frame)
        {
            var origin = (IntPtr)(p + (rect.Y * stride) + (rect.X * 4));
            using var pixmap = new SKPixmap(info, origin, stride);
            using var data = pixmap.Encode(SKEncodedImageFormat.Jpeg, quality)
                ?? throw new InvalidOperationException("Не удалось закодировать JPEG");
            return data.ToArray();
        }
    }

    /// <summary>Декодирует JPEG в <paramref name="target"/> (BGRA, плотная упаковка width*4).</summary>
    public static unsafe void Decode(byte[] jpeg, byte[] target, int width, int height)
    {
        if (target.Length < width * height * 4)
        {
            throw new ArgumentException("Буфер меньше изображения", nameof(target));
        }

        using var data = SKData.CreateCopy(jpeg);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("Повреждённые данные JPEG");
        if (codec.Info.Width != width || codec.Info.Height != height)
        {
            throw new InvalidDataException("Размер JPEG не совпадает с заявленным");
        }

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        fixed (byte* p = target)
        {
            var result = codec.GetPixels(info, (IntPtr)p);
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                throw new InvalidDataException($"Ошибка декодирования JPEG: {result}");
            }
        }
    }
}

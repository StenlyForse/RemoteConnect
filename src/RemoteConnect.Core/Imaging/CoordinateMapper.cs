namespace RemoteConnect.Core.Imaging;

/// <summary>Пересчёт координат мыши между окном просмотра и удалённым монитором (FR-INP-1).</summary>
public static class CoordinateMapper
{
    public const int NormalizedMax = ushort.MaxValue;

    /// <summary>Точка внутри отображаемой картинки размером <paramref name="width"/>×<paramref name="height"/> → 0..65535.</summary>
    public static (ushort X, ushort Y) ToNormalized(double x, double y, double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return (0, 0);
        }

        return (Normalize(x / width), Normalize(y / height));
    }

    /// <summary>0..65535 → пиксель монитора с началом в (<paramref name="left"/>, <paramref name="top"/>).</summary>
    public static (int X, int Y) ToPixel(ushort nx, ushort ny, int left, int top, int width, int height)
    {
        var x = left + (int)Math.Round(nx / (double)NormalizedMax * (width - 1));
        var y = top + (int)Math.Round(ny / (double)NormalizedMax * (height - 1));
        return (x, y);
    }

    private static ushort Normalize(double fraction) =>
        (ushort)Math.Round(Math.Clamp(fraction, 0, 1) * NormalizedMax);
}

namespace RemoteConnect.Core.Imaging;

public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>
/// Поиск изменившихся областей между двумя кадрами BGRA (FR-SCR-2).
/// Кадр делится на плитки; соседние изменённые плитки в строке объединяются в полосы,
/// а одинаковые полосы в соседних строках — в прямоугольники.
/// </summary>
public static class TileDiffer
{
    public const int DefaultTileSize = 64;
    private const int BytesPerPixel = 4;

    public static List<PixelRect> FindDirtyRects(
        ReadOnlySpan<byte> previous,
        ReadOnlySpan<byte> current,
        int width,
        int height,
        int stride,
        int tileSize = DefaultTileSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * BytesPerPixel);
        if (previous.Length < stride * height || current.Length < stride * height)
        {
            throw new ArgumentException("Буфер меньше размера кадра");
        }

        var result = new List<PixelRect>();
        var open = new List<PixelRect>();
        var rowRuns = new List<PixelRect>();

        for (var tileY = 0; tileY < height; tileY += tileSize)
        {
            var tileHeight = Math.Min(tileSize, height - tileY);
            rowRuns.Clear();
            var runStart = -1;

            for (var tileX = 0; tileX < width; tileX += tileSize)
            {
                var tileWidth = Math.Min(tileSize, width - tileX);
                var dirty = IsTileDirty(previous, current, stride, tileX, tileY, tileWidth, tileHeight);
                if (dirty && runStart < 0)
                {
                    runStart = tileX;
                }
                else if (!dirty && runStart >= 0)
                {
                    rowRuns.Add(new PixelRect(runStart, tileY, tileX - runStart, tileHeight));
                    runStart = -1;
                }
            }

            if (runStart >= 0)
            {
                rowRuns.Add(new PixelRect(runStart, tileY, width - runStart, tileHeight));
            }

            MergeRows(open, rowRuns, result);
        }

        result.AddRange(open);
        return result;
    }

    public static PixelRect Full(int width, int height) => new(0, 0, width, height);

    private static void MergeRows(List<PixelRect> open, List<PixelRect> rowRuns, List<PixelRect> result)
    {
        var next = new List<PixelRect>(rowRuns.Count);
        foreach (var run in rowRuns)
        {
            var index = open.FindIndex(o => o.X == run.X && o.Width == run.Width && o.Y + o.Height == run.Y);
            if (index >= 0)
            {
                var o = open[index];
                open.RemoveAt(index);
                next.Add(o with { Height = o.Height + run.Height });
            }
            else
            {
                next.Add(run);
            }
        }

        result.AddRange(open);
        open.Clear();
        open.AddRange(next);
    }

    private static bool IsTileDirty(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> current, int stride, int x, int y, int w, int h)
    {
        var rowBytes = w * BytesPerPixel;
        for (var row = 0; row < h; row++)
        {
            var offset = (y + row) * stride + x * BytesPerPixel;
            if (!previous.Slice(offset, rowBytes).SequenceEqual(current.Slice(offset, rowBytes)))
            {
                return true;
            }
        }

        return false;
    }
}

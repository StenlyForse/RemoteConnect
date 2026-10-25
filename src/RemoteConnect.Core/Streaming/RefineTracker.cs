using RemoteConnect.Core.Imaging;

namespace RemoteConnect.Core.Streaming;

/// <summary>
/// Дорисовка чёткости (FR-ADP-5, FR-ADP-8): помнит, с каким качеством и когда передана каждая плитка,
/// и выдаёт порцию плиток, которые были переданы хуже порога и с тех пор не менялись. Не потокобезопасен —
/// используется только циклом трансляции.
/// </summary>
public sealed class RefineTracker(TimeProvider time)
{
    /// <summary>Сколько плитка должна не меняться, чтобы её дорисовать.</summary>
    public static readonly TimeSpan StableFor = TimeSpan.FromSeconds(1);

    private int _width;
    private int _height;
    private int _columns;
    private int _rows;
    private int _tileSize = TileDiffer.DefaultTileSize;
    private byte[] _quality = [];
    private long[] _sentAt = [];

    /// <summary>Новый размер кадра (смена монитора): все плитки считаются непереданными.</summary>
    public void Reset(int width, int height, int tileSize = TileDiffer.DefaultTileSize)
    {
        (_width, _height, _tileSize) = (width, height, tileSize);
        _columns = (width + tileSize - 1) / tileSize;
        _rows = (height + tileSize - 1) / tileSize;
        _quality = new byte[_columns * _rows];
        _sentAt = new long[_columns * _rows];
        Array.Fill(_quality, byte.MaxValue);
    }

    /// <summary>Отмечает плитки, которые покрывают отправленные прямоугольники.</summary>
    public void OnSent(IEnumerable<PixelRect> rects, int quality)
    {
        var now = time.GetTimestamp();
        foreach (var rect in rects)
        {
            var x0 = rect.X / _tileSize;
            var y0 = rect.Y / _tileSize;
            var x1 = Math.Min(_columns - 1, (rect.X + rect.Width - 1) / _tileSize);
            var y1 = Math.Min(_rows - 1, (rect.Y + rect.Height - 1) / _tileSize);
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    _quality[(y * _columns) + x] = (byte)quality;
                    _sentAt[(y * _columns) + x] = now;
                }
            }
        }
    }

    /// <summary>
    /// Плитки для дорисовки качеством <paramref name="threshold"/>: переданы хуже него и не менялись <see cref="StableFor"/>.
    /// Не больше <paramref name="maxAreaFraction"/> площади кадра за раз; соседние плитки в строке объединяются.
    /// </summary>
    public List<PixelRect> TakeRefinement(int threshold, double maxAreaFraction)
    {
        var result = new List<PixelRect>();
        if (_columns == 0)
        {
            return result;
        }

        var now = time.GetTimestamp();
        var budget = (long)(_width * (long)_height * maxAreaFraction);
        long area = 0;
        for (var y = 0; y < _rows; y++)
        {
            var runStart = -1;
            for (var x = 0; x <= _columns; x++)
            {
                var candidate = x < _columns && IsCandidate((y * _columns) + x, threshold, now);
                if (candidate && runStart < 0)
                {
                    runStart = x;
                }

                if (!candidate && runStart >= 0)
                {
                    var rect = TileRun(runStart, x, y);
                    var rectArea = (long)rect.Width * rect.Height;
                    if (area + rectArea > budget && result.Count > 0)
                    {
                        return result;
                    }

                    result.Add(rect);
                    area += rectArea;
                    runStart = -1;
                }
            }
        }

        return result;
    }

    private bool IsCandidate(int index, int threshold, long now) =>
        _quality[index] < threshold && time.GetElapsedTime(_sentAt[index], now) >= StableFor;

    private PixelRect TileRun(int fromColumn, int toColumn, int row)
    {
        var x = fromColumn * _tileSize;
        var y = row * _tileSize;
        return new PixelRect(x, y, Math.Min(toColumn * _tileSize, _width) - x, Math.Min(_tileSize, _height - y));
    }
}

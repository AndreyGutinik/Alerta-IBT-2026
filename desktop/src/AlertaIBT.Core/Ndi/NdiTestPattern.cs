namespace AlertaIBT.Core.Ndi;

/// <summary>
/// The built-in "barras de teste": seven color bars over a gray ramp band, a white border, a tick every 256 pixels along the top, and a white
/// marker that sweeps across (so the owner sees at the church that the picture moves, that Holyrics' transparent window really shows over it and
/// that the alert bar stays on top). Pure pixel code (BGRA), no allocation per frame once the base picture exists.
/// </summary>
public sealed class NdiTestPattern
{
    /// <summary>The seven bars, left to right: white, yellow, cyan, green, magenta, red, blue (B, G, R).</summary>
    public static readonly (byte B, byte G, byte R)[] Bars =
    [
        (255, 255, 255), (0, 255, 255), (255, 255, 0), (0, 255, 0), (255, 0, 255), (0, 0, 255), (255, 0, 0),
    ];

    /// <summary>Width of the moving marker in pixels.</summary>
    public const int MarkerWidth = 24;

    /// <summary>How fast the marker sweeps, in pixels per second.</summary>
    public const double MarkerSpeed = 480;

    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _baseline;

    /// <summary>Builds the static part for a target of this size.</summary>
    public NdiTestPattern(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 64);

        _width = width;
        _height = height;
        _baseline = new byte[width * height * 4];
        DrawBase();
    }

    /// <summary>Width.</summary>
    public int Width => _width;

    /// <summary>Height.</summary>
    public int Height => _height;

    /// <summary>The x of the marker's left edge at <paramref name="seconds"/>.</summary>
    public int MarkerX(double seconds)
    {
        var span = _width - MarkerWidth - 2 * Border;
        var travel = MarkerSpeed * seconds % (2.0 * span);
        var pos = travel <= span ? travel : 2.0 * span - travel;
        return Border + (int)pos;
    }

    /// <summary>Border thickness in pixels.</summary>
    public static int Border => 6;

    /// <summary>Writes the picture at <paramref name="seconds"/> into <paramref name="destination"/> (at least <c>Width*Height*4</c> bytes, stride <c>Width*4</c>).</summary>
    public void Render(Span<byte> destination, double seconds)
    {
        _baseline.CopyTo(destination);
        var x0 = MarkerX(seconds);
        var stride = _width * 4;
        for (var y = Border; y < _height - Border; y++)
        {
            var row = y * stride + x0 * 4;
            for (var x = 0; x < MarkerWidth; x++)
            {
                var o = row + x * 4;
                destination[o] = 255;
                destination[o + 1] = 255;
                destination[o + 2] = 255;
                destination[o + 3] = 255;
            }
        }
    }

    private void DrawBase()
    {
        var stride = _width * 4;
        var barsBottom = _height * 2 / 3;
        for (var y = 0; y < _height; y++)
        {
            for (var x = 0; x < _width; x++)
            {
                byte b;
                byte g;
                byte r;
                if (y < barsBottom)
                {
                    var bar = Bars[Math.Min(Bars.Length - 1, x * Bars.Length / _width)];
                    (b, g, r) = bar;
                }
                else
                {
                    var gray = (byte)(x * 255 / Math.Max(1, _width - 1));
                    b = g = r = gray;
                }

                // A tick every 256 px along the top and a darker line where the first 56 px (the alert bar) end.
                if ((y < 24 && x % 256 < 4) || y == 56)
                {
                    b = g = r = 0;
                }

                if (x < Border || y < Border || x >= _width - Border || y >= _height - Border)
                {
                    b = g = r = 255;
                }

                var o = y * stride + x * 4;
                _baseline[o] = b;
                _baseline[o + 1] = g;
                _baseline[o + 2] = r;
                _baseline[o + 3] = 255;
            }
        }
    }
}

using System.Text.RegularExpressions;

namespace AlertaIBT.Core.Ndi;

/// <summary>A small picture of a camera for the UI: BGRA, 480 pixels wide, <c>Stride = Width * 4</c>.</summary>
/// <param name="Width">Width (480).</param>
/// <param name="Height">Height (270 for a 16:9 source).</param>
/// <param name="Pixels">The pixels, owned by the caller (a copy).</param>
/// <param name="Version">Grows with every new picture, so the UI can skip an unchanged one.</param>
public sealed record NdiThumbnail(int Width, int Height, byte[] Pixels, long Version);

/// <summary>
/// One reusable thumbnail buffer, fed at 20 fps at most. The picture is box-filtered (4 samples inside each source block), so it costs a
/// fraction of a millisecond, and no memory is allocated per picture.
/// </summary>
public sealed class ThumbnailBuffer
{
    /// <summary>Thumbnail width in pixels.</summary>
    public const int ThumbWidth = 480;

    private const int MaxHeight = 480;

    private readonly object _gate = new();
    private readonly byte[] _pixels = new byte[ThumbWidth * MaxHeight * 4];
    private int[] _xs = [];
    private int _xsFor;
    private int _width;
    private int _height;
    private long _version;
    private long _lastTick;

    /// <summary>Whether a picture was ever stored.</summary>
    public bool HasPicture
    {
        get
        {
            lock (_gate)
            {
                return _version > 0;
            }
        }
    }

    /// <summary>The number of the latest picture.</summary>
    public long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    /// <summary>Stores a downscaled copy of <paramref name="frame"/> unless one was stored less than <paramref name="minInterval"/> ago. True when stored.</summary>
    public bool Update(in NdiFrameView frame, TimeSpan minInterval, TimeProvider clock)
    {
        var sw = frame.Width;
        var sh = frame.Height;
        if (sw <= 0 || sh <= 0 || frame.Data.Length < frame.Stride * sh)
        {
            return false;
        }

        var now = clock.GetTimestamp();
        if (_lastTick != 0 && clock.GetElapsedTime(_lastTick, now) < minInterval)
        {
            return false;
        }

        var tw = ThumbWidth;
        var th = Math.Clamp((int)Math.Round((double)ThumbWidth * sh / sw), 1, MaxHeight);

        lock (_gate)
        {
            if (_xsFor != sw || _xs.Length != tw)
            {
                _xs = new int[tw];
                for (var x = 0; x < tw; x++)
                {
                    _xs[x] = (int)((long)x * sw / tw);
                }

                _xsFor = sw;
            }

            var data = frame.Data;
            var stride = frame.Stride;
            for (var y = 0; y < th; y++)
            {
                var y0 = (int)((long)y * sh / th);
                var y1 = Math.Min(sh - 1, y0 + Math.Max(1, sh / th / 2));
                var row0 = y0 * stride;
                var row1 = y1 * stride;
                var dst = y * tw * 4;
                for (var x = 0; x < tw; x++)
                {
                    var x0 = _xs[x] * 4;
                    var x1 = Math.Min(sw - 1, _xs[x] + Math.Max(1, sw / tw / 2)) * 4;
                    var o = dst + x * 4;
                    _pixels[o] = (byte)((data[row0 + x0] + data[row0 + x1] + data[row1 + x0] + data[row1 + x1]) >> 2);
                    _pixels[o + 1] = (byte)((data[row0 + x0 + 1] + data[row0 + x1 + 1] + data[row1 + x0 + 1] + data[row1 + x1 + 1]) >> 2);
                    _pixels[o + 2] = (byte)((data[row0 + x0 + 2] + data[row0 + x1 + 2] + data[row1 + x0 + 2] + data[row1 + x1 + 2]) >> 2);
                    _pixels[o + 3] = 255;
                }
            }

            _width = tw;
            _height = th;
            _version++;
            _lastTick = now;
        }

        return true;
    }

    /// <summary>Copies the picture into <paramref name="destination"/> (at least <c>Width*Height*4</c> bytes). False when there is none or it does not fit.</summary>
    public bool TryCopy(Span<byte> destination, out int width, out int height, out long version)
    {
        lock (_gate)
        {
            width = _width;
            height = _height;
            version = _version;
            var size = _width * _height * 4;
            if (_version == 0 || destination.Length < size)
            {
                return false;
            }

            _pixels.AsSpan(0, size).CopyTo(destination);
            return true;
        }
    }

    /// <summary>A copy of the picture as a new object, or <c>null</c> when none was stored.</summary>
    public NdiThumbnail? Snapshot()
    {
        lock (_gate)
        {
            if (_version == 0)
            {
                return null;
            }

            return new NdiThumbnail(_width, _height, _pixels.AsSpan(0, _width * _height * 4).ToArray(), _version);
        }
    }

    /// <summary>Stores <paramref name="picture"/> as the current one (used to keep the last picture of a camera that left its lane).</summary>
    public void Restore(NdiThumbnail picture)
    {
        ArgumentNullException.ThrowIfNull(picture);

        lock (_gate)
        {
            picture.Pixels.CopyTo(_pixels, 0);
            _width = picture.Width;
            _height = picture.Height;
            _version++;
        }
    }
}

/// <summary>Reads what the user typed as a camera source.</summary>
public static partial class NdiSourceParser
{
    /// <summary>
    /// A name such as <c>MACHINE (Camera)</c> is a name; text without parentheses and without spaces (<c>192.168.1.50</c>, <c>192.168.1.50:5961</c>,
    /// <c>camera.local</c>) is a direct address. Blank is <c>null</c>.
    /// </summary>
    public static NdiSourceRef? Parse(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return Address().IsMatch(trimmed) ? new NdiSourceRef(null, trimmed) : new NdiSourceRef(trimmed, null);
    }

    /// <summary>Whether two source texts name the same camera (blank never matches; case does not matter).</summary>
    public static bool Same(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[A-Za-z0-9\-\.\[\]:]+$")]
    private static partial Regex Address();
}

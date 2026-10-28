using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AlertaIBT.Core.Player;

namespace AlertaIBT.App.Services;

/// <summary>Loads the cover picture of a track as a bitmap ready to show.</summary>
public interface ICoverArtLoader
{
    /// <summary>The cover, decoded off the UI thread at most <see cref="CoverArtService.MaxSide"/> pixels on its short side and frozen; <c>null</c> when the track has none. Never throws.</summary>
    Task<ImageSource?> LoadAsync(string trackPath);
}

/// <summary>
/// The cover loader of the app: <see cref="ICoverArtReader"/> finds the bytes (embedded picture or the folder's cover.jpg), a worker thread
/// decodes them scaled down (336 px is twice the 168 px the screen draws) and freezes the bitmap so any thread may hand it to the UI. The
/// last few covers (and the tracks that have none) are remembered, so going back to a track costs nothing.
/// </summary>
public sealed class CoverArtService : ICoverArtLoader
{
    /// <summary>The short side of the decoded bitmap, in pixels.</summary>
    public const int MaxSide = 336;

    private readonly ICoverArtReader _reader;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="reader">Finds the cover bytes.</param>
    /// <param name="capacity">How many tracks are remembered.</param>
    public CoverArtService(ICoverArtReader reader, int capacity = 8)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _reader = reader;
        _capacity = capacity;
    }

    /// <summary>How many tracks are remembered now.</summary>
    public int CachedCount
    {
        get
        {
            lock (_gate)
            {
                return _cache.Count;
            }
        }
    }

    /// <inheritdoc />
    public Task<ImageSource?> LoadAsync(string trackPath)
    {
        if (string.IsNullOrWhiteSpace(trackPath))
        {
            return Task.FromResult<ImageSource?>(null);
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(trackPath, out var known))
            {
                Touch(trackPath);
                return Task.FromResult(known);
            }
        }

        return Task.Run(() =>
        {
            ImageSource? image = null;
            try
            {
                if (_reader.Read(trackPath) is { Length: > 0 } bytes)
                {
                    image = Decode(bytes);
                }
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException or FileFormatException or UnauthorizedAccessException)
            {
                image = null; // a picture that cannot be read is no picture
            }

            Remember(trackPath, image);
            return image;
        });
    }

    /// <summary>Decodes the bytes of a JPEG or PNG scaled so the short side is at most <see cref="MaxSide"/>, and freezes it.</summary>
    public static BitmapSource Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        int width;
        int height;
        using (var probe = new MemoryStream(bytes))
        {
            var frame = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.StreamSource = new MemoryStream(bytes);
        var shortSide = Math.Min(width, height);
        if (shortSide > MaxSide)
        {
            // Scale by the width; the height follows (the screen crops a non-square cover to a square).
            bitmap.DecodePixelWidth = (int)Math.Ceiling((double)width * MaxSide / shortSide);
        }

        bitmap.EndInit();
        bitmap.StreamSource.Dispose();
        bitmap.Freeze();
        return bitmap;
    }

    private void Remember(string path, ImageSource? image)
    {
        lock (_gate)
        {
            _cache[path] = image;
            Touch(path);
            while (_order.Count > _capacity)
            {
                var oldest = _order.Last!.Value;
                _order.RemoveLast();
                _cache.Remove(oldest);
            }
        }
    }

    // Caller holds the lock: the path becomes the most recently used.
    private void Touch(string path)
    {
        var node = _order.First;
        while (node is not null)
        {
            if (string.Equals(node.Value, path, StringComparison.OrdinalIgnoreCase))
            {
                _order.Remove(node);
                break;
            }

            node = node.Next;
        }

        _order.AddFirst(path);
    }
}

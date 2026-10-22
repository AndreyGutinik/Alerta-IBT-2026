using System.Windows;
using System.Windows.Media.Imaging;
using DrawingIcon = System.Drawing.Icon;

namespace AlertaIBT.App.Theme;

/// <summary>
/// The app icon (<c>app.ico</c>, one PNG frame per size from 16 to 256 px, rendered from the vector logo). Every place that
/// shows it asks for the frame that matches the pixels it really draws, so the logo is never a 256 px frame shrunk by WPF or
/// Windows (blurry in the tray) nor a 16 px frame stretched (blocky in the title bar).
/// </summary>
public static class AppIcon
{
    /// <summary>The icon resource inside the program.</summary>
    public static readonly Uri ResourceUri = new("pack://application:,,,/AlertaIBT;component/Assets/app.ico", UriKind.Absolute);

    /// <summary>
    /// The frame to use for <paramref name="wanted"/> pixels: the exact size, else the smallest one that is bigger (it is
    /// shrunk, which stays sharp), else the biggest there is. Returns the index into <paramref name="sizes"/>.
    /// </summary>
    internal static int PickFrame(IReadOnlyList<int> sizes, int wanted)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        if (sizes.Count == 0)
        {
            throw new ArgumentException("The icon has no frames.", nameof(sizes));
        }

        var best = -1;
        for (var i = 0; i < sizes.Count; i++)
        {
            if (sizes[i] >= wanted && (best < 0 || sizes[i] < sizes[best]))
            {
                best = i;
            }
        }

        if (best >= 0)
        {
            return best;
        }

        best = 0;
        for (var i = 1; i < sizes.Count; i++)
        {
            if (sizes[i] > sizes[best])
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>The icon frame for <paramref name="pixels"/> device pixels, as a WPF image (frozen).</summary>
    public static BitmapFrame Frame(int pixels)
    {
        var decoder = BitmapDecoder.Create(ResourceUri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frames = decoder.Frames;
        var frame = frames[PickFrame([.. frames.Select(f => f.PixelWidth)], pixels)];
        frame.Freeze();
        return frame;
    }

    /// <summary>The icon frame for a square of <paramref name="dips"/> device-independent pixels at <paramref name="dpiScale"/> (1.0 = 96 dpi).</summary>
    public static BitmapFrame Frame(double dips, double dpiScale) => Frame((int)Math.Ceiling(dips * Math.Max(dpiScale, 1.0)));

    /// <summary>
    /// The size of the notification-area icon on this PC right now (16 at 100 percent, 20/24/32 at higher scales), from the
    /// system's small-icon metric (<c>SM_CXSMICON</c>, in pixels; <c>SystemParameters.SmallIconWidth</c> is in device-independent units).
    /// </summary>
    public static int TrayPixels => Math.Max(16, Platform.NativeMethods.GetSystemMetrics(Platform.NativeMethods.SM_CXSMICON));

    /// <summary>The tray icon: the frame of <paramref name="pixels"/> (default <see cref="TrayPixels"/>), picked by size, not left to Windows to shrink.</summary>
    public static DrawingIcon LoadTrayIcon(int? pixels = null)
    {
        var info = Application.GetResourceStream(ResourceUri)
            ?? throw new InvalidOperationException("The app icon resource is missing.");
        using var stream = info.Stream;
        var size = pixels ?? TrayPixels;
        return new DrawingIcon(stream, new System.Drawing.Size(size, size));
    }
}

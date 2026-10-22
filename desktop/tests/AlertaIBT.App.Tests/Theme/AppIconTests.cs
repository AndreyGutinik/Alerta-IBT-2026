using System.Buffers.Binary;
using AlertaIBT.App.Theme;

namespace AlertaIBT.App.Tests.Theme;

public sealed class AppIconTests
{
    private static readonly int[] ExpectedSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static string IconPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "icons", "app.ico")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "assets", "icons", "app.ico");
    }

    // The ICO directory: (declared width, declared height, bit count, offset, length) of each frame.
    private static List<(int Width, int Height, int Bits, int Offset, int Length)> Directory(byte[] ico)
    {
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(0, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2, 2))); // type 1 = icon
        var count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4, 2));
        var frames = new List<(int, int, int, int, int)>();
        for (var i = 0; i < count; i++)
        {
            var entry = ico.AsSpan(6 + (i * 16), 16);
            frames.Add((
                entry[0] == 0 ? 256 : entry[0],
                entry[1] == 0 ? 256 : entry[1],
                BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(6, 2)),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(12, 4)),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8, 4))));
        }

        return frames;
    }

    [Fact]
    public void The_ico_holds_every_size_from_16_to_256_as_a_png_of_exactly_that_size()
    {
        var ico = File.ReadAllBytes(IconPath());
        var frames = Directory(ico);

        Assert.Equal(ExpectedSizes, frames.Select(f => f.Width).Order());
        foreach (var frame in frames)
        {
            Assert.Equal(frame.Width, frame.Height);
            Assert.Equal(32, frame.Bits);
            var data = ico.AsSpan(frame.Offset, frame.Length);
            Assert.True(data[..8].SequenceEqual(PngSignature), $"frame {frame.Width} is not a PNG");

            // IHDR follows the signature: length, "IHDR", width, height (big endian).
            Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(data.Slice(12, 4)));
            Assert.Equal(frame.Width, (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4)));
            Assert.Equal(frame.Height, (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4)));

            // The whole PNG is there and decodes (a truncated frame would not).
            using var stream = new MemoryStream(data.ToArray());
            var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            Assert.Equal(frame.Width, decoder.Frames[0].PixelWidth);
            Assert.Equal(frame.Height, decoder.Frames[0].PixelHeight);
        }
    }

    [Fact]
    public void The_png_copies_exist_at_256_and_512()
    {
        var folder = Path.GetDirectoryName(IconPath())!;
        foreach (var size in new[] { 256, 512 })
        {
            var data = File.ReadAllBytes(Path.Combine(folder, $"app-{size}.png"));
            Assert.True(data.AsSpan(0, 8).SequenceEqual(PngSignature));
            Assert.Equal(size, (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4)));
            Assert.Equal(size, (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20, 4)));
        }
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(18, 20)] // no 18: the next one up, shrunk
    [InlineData(20, 20)]
    [InlineData(30, 32)]
    [InlineData(256, 256)]
    [InlineData(400, 256)] // bigger than any frame: the biggest
    public void The_frame_picked_is_the_exact_size_else_the_next_one_up_else_the_biggest(int wanted, int expected)
    {
        var sizes = new[] { 256, 16, 32, 20, 24 }; // in any order
        Assert.Equal(expected, sizes[AppIcon.PickFrame(sizes, wanted)]);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void The_tray_icon_is_the_frame_of_the_tray_size_not_a_big_one_shrunk(int pixels)
    {
        WpfTestHost.Run(() =>
        {
            using var icon = AppIcon.LoadTrayIcon(pixels);
            Assert.Equal(pixels, icon.Width);
            Assert.Equal(pixels, icon.Height);
        });
    }

    [Theory]
    [InlineData(16, 1.0, 16)]
    [InlineData(24, 1.0, 24)]
    [InlineData(24, 1.5, 40)] // 36 px wanted: the 40 frame, shrunk
    [InlineData(16, 2.0, 32)]
    public void A_window_logo_gets_the_frame_for_the_pixels_it_draws(double dips, double scale, int expected)
    {
        WpfTestHost.Run(() => Assert.Equal(expected, AppIcon.Frame(dips, scale).PixelWidth));
    }
}

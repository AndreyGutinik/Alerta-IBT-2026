using System.Buffers.Binary;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AlertaIBT.App.Services;
using AlertaIBT.Core.Player;

namespace AlertaIBT.App.Tests.Player;

// The album art: the reader finds the picture (FLAC picture block, MP3 APIC, M4A covr, then cover.jpg in the folder) and the service decodes it
// off the UI thread, scaled to 336 px, frozen and remembered. Real files (a 2 s FLAC, a 1 s MP3 and M4A made with ffmpeg) and no sound.
public sealed class CoverArtTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-cover-" + Guid.NewGuid().ToString("N"));

    public CoverArtTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A scanner may hold a file for a moment.
        }
    }

    private static byte[] Png(Color color, int width = 64, int height = 64)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = 255;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // Puts a FLAC PICTURE block (metadata block 6) after the last block that is there.
    private static byte[] WithPicture(byte[] flac, byte[] image, int pictureType, string description)
    {
        var position = 4;
        var lastHeader = 0;
        while (true)
        {
            lastHeader = position;
            var last = (flac[position] & 0x80) != 0;
            var length = (flac[position + 1] << 16) | (flac[position + 2] << 8) | flac[position + 3];
            position += 4 + length;
            if (last)
            {
                break;
            }
        }

        using var block = new MemoryStream();
        void Int(int value)
        {
            Span<byte> four = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(four, value);
            block.Write(four);
        }

        var mime = "image/png"u8.ToArray();
        var text = System.Text.Encoding.UTF8.GetBytes(description);
        Int(pictureType);
        Int(mime.Length);
        block.Write(mime);
        Int(text.Length);
        block.Write(text);
        Int(64);
        Int(64);
        Int(32);
        Int(0);
        Int(image.Length);
        block.Write(image);

        var result = new List<byte>(flac.Length + (int)block.Length + 4);
        result.AddRange(flac.AsSpan(0, position).ToArray());
        result[lastHeader] = (byte)(result[lastHeader] & 0x7F); // it is no longer the last block
        result.Add(0x80 | 6);
        result.Add((byte)((block.Length >> 16) & 0xFF));
        result.Add((byte)((block.Length >> 8) & 0xFF));
        result.Add((byte)(block.Length & 0xFF));
        result.AddRange(block.ToArray());
        result.AddRange(flac.AsSpan(position).ToArray());
        return [.. result];
    }

    private string Copy(string asset, string name)
    {
        var path = Path.Combine(_dir, name);
        File.Copy(PlayerPipelineTests.Asset(asset), path, overwrite: true);
        return path;
    }

    private string FlacWith(string name, params (int Type, byte[] Image)[] pictures)
    {
        var bytes = File.ReadAllBytes(PlayerPipelineTests.Asset("tone-24-48.flac"));
        foreach (var (type, image) in pictures)
        {
            bytes = WithPicture(bytes, image, type, "capa");
        }

        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static Color Pixel(byte[] png)
    {
        var frame = BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var one = new byte[4];
        new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(1, 1, 1, 1), one, 4, 0);
        return Color.FromRgb(one[2], one[1], one[0]);
    }

    private static void AssertGreenish(Color color)
    {
        Assert.InRange(color.R, 0x1D - 6, 0x1D + 6);
        Assert.InRange(color.G, 0xA3 - 6, 0xA3 + 6);
        Assert.InRange(color.B, 0x5F - 6, 0x5F + 6);
    }

    private static readonly Color Blue = Color.FromRgb(0x20, 0x40, 0xC0);
    private static readonly Color Red = Color.FromRgb(0xC0, 0x30, 0x20);

    // ---- the reader ----

    [Fact]
    public void A_flac_with_a_front_cover_gives_that_picture_even_when_another_one_comes_first()
    {
        var path = FlacWith("a.flac", (0, Png(Red)), (3, Png(Blue)));

        var bytes = new CoverArtReader().Read(path);

        Assert.NotNull(bytes);
        Assert.Equal(Blue, Pixel(bytes!));
    }

    [Fact]
    public void A_flac_with_only_a_non_front_picture_still_gives_it_and_a_back_cover_is_the_last_choice()
    {
        var other = FlacWith("o.flac", (0, Png(Red)));
        var back = FlacWith("b.flac", (4, Png(Red)), (0, Png(Blue)));

        Assert.Equal(Red, Pixel(new CoverArtReader().Read(other)!));
        Assert.Equal(Blue, Pixel(new CoverArtReader().Read(back)!));
    }

    [Fact]
    public void A_flac_without_a_picture_has_none()
    {
        var path = Copy("tone-24-48.flac", "plain.flac");

        Assert.Null(new CoverArtReader().Read(path));
    }

    [Fact]
    public void Without_an_embedded_picture_the_folder_cover_is_used_and_cover_beats_folder()
    {
        var path = Copy("tone-24-48.flac", "song.flac");
        Assert.Null(new CoverArtReader().Read(path));

        File.WriteAllBytes(Path.Combine(_dir, "Folder.JPG"), Png(Red));
        Assert.Equal(Red, Pixel(new CoverArtReader().Read(path)!));

        File.WriteAllBytes(Path.Combine(_dir, "cover.png"), Png(Blue));
        Assert.Equal(Blue, Pixel(new CoverArtReader().Read(path)!));
    }

    [Fact]
    public void The_embedded_picture_wins_over_the_folder_cover()
    {
        File.WriteAllBytes(Path.Combine(_dir, "cover.jpg"), Png(Red));
        var path = FlacWith("e.flac", (3, Png(Blue)));

        Assert.Equal(Blue, Pixel(new CoverArtReader().Read(path)!));
    }

    [Fact]
    public void An_mp3_with_an_apic_and_an_m4a_with_a_covr_give_their_picture()
    {
        var mp3 = Copy("tone-cover.mp3", "x.mp3");
        var m4a = Copy("tone-cover.m4a", "x.m4a");

        var fromMp3 = new CoverArtReader().Read(mp3);
        var fromM4a = new CoverArtReader().Read(m4a);

        Assert.NotNull(fromMp3);
        Assert.NotNull(fromM4a);
        AssertGreenish(Pixel(fromMp3!)); // ffmpeg made the picture through YUV, so the green is a hair off
        AssertGreenish(Pixel(fromM4a!));
    }

    [Fact]
    public void A_missing_corrupt_or_blank_file_has_no_cover_and_never_throws()
    {
        var corrupt = Path.Combine(_dir, "bad.flac");
        File.WriteAllBytes(corrupt, [1, 2, 3, 4, 5, 6, 7, 8]);
        var reader = new CoverArtReader();

        Assert.Null(reader.Read(Path.Combine(_dir, "missing.flac")));
        Assert.Null(reader.Read(corrupt));
        Assert.Null(reader.Read(string.Empty));
        Assert.Null(reader.Read(Path.Combine(_dir, "no-such-folder", "x.flac")));
    }

    // ---- the service ----

    [Fact]
    public async Task The_service_decodes_a_big_cover_down_to_336_pixels_frozen_off_the_ui_thread()
    {
        var path = FlacWith("big.flac", (3, Png(Blue, 1000, 1000)));
        var service = new CoverArtService(new CoverArtReader());

        var image = await service.LoadAsync(path);

        var bitmap = Assert.IsAssignableFrom<BitmapSource>(image);
        Assert.Equal(336, bitmap.PixelWidth);
        Assert.Equal(336, bitmap.PixelHeight);
        Assert.True(bitmap.IsFrozen);
    }

    [Fact]
    public async Task The_service_keeps_a_small_cover_as_it_is_and_scales_a_wide_one_by_its_short_side()
    {
        var small = await new CoverArtService(new CoverArtReader()).LoadAsync(FlacWith("s.flac", (3, Png(Blue, 100, 100))));
        var wide = await new CoverArtService(new CoverArtReader()).LoadAsync(FlacWith("w.flac", (3, Png(Blue, 1000, 500))));

        Assert.Equal(100, ((BitmapSource)small!).PixelWidth);
        Assert.Equal(672, ((BitmapSource)wide!).PixelWidth);
        Assert.Equal(336, ((BitmapSource)wide).PixelHeight);
    }

    [Fact]
    public async Task The_service_returns_null_for_no_art_and_remembers_only_the_last_few()
    {
        var reader = new CountingReader();
        var service = new CoverArtService(reader, capacity: 3);
        var paths = Enumerable.Range(1, 5).Select(i => Path.Combine(_dir, $"t{i}.flac")).ToList();

        Assert.Null(await service.LoadAsync(Path.Combine(_dir, "none.flac")));
        foreach (var path in paths)
        {
            Assert.NotNull(await service.LoadAsync(path));
        }

        Assert.Equal(3, service.CachedCount);
        var before = reader.Reads;
        await service.LoadAsync(paths[4]); // the newest: remembered
        Assert.Equal(before, reader.Reads);
        await service.LoadAsync(paths[0]); // the oldest: read again
        Assert.Equal(before + 1, reader.Reads);
    }

    [Fact]
    public async Task A_damaged_picture_is_no_picture()
    {
        var service = new CoverArtService(new CountingReader(garbage: true));

        Assert.Null(await service.LoadAsync(Path.Combine(_dir, "x.flac")));
    }

    private sealed class CountingReader(bool garbage = false) : ICoverArtReader
    {
        private int _reads;

        public int Reads => _reads;

        public byte[]? Read(string trackPath)
        {
            Interlocked.Increment(ref _reads);
            if (Path.GetFileName(trackPath) == "none.flac")
            {
                return null;
            }

            return garbage ? [1, 2, 3] : Png(Blue, 32, 32);
        }
    }
}

using System.Runtime.InteropServices;
using AlertaIBT.App.Ndi;
using AlertaIBT.Core.Ndi;

namespace AlertaIBT.App.Tests.Ndi;

/// <summary>
/// Talks to the REAL NDI runtime when NDI Tools is installed on this PC (and skips cleanly when not): a sender made with the runtime itself
/// sends a 1920x1080 color-bar pattern and the app's finder and receiver get it back. The sender exists only in this test file: the app never sends.
/// </summary>
public sealed unsafe class NdiRealRuntimeTests
{
    private const int W = 1920;
    private const int H = 1080;

    // The same colors every bar test uses (B, G, R).
    private static readonly (byte B, byte G, byte R)[] Bars =
    [
        (255, 255, 255), (0, 255, 255), (255, 255, 0), (0, 255, 0), (255, 0, 255), (0, 0, 255), (255, 0, 0), (0, 0, 0),
    ];

    private static NdiLibrary LoadOrSkip()
    {
        var library = NdiLibrary.Load();
        Assert.SkipUnless(library.IsAvailable, "Runtime do NDI nao instalado neste PC");
        return library;
    }

    [Fact]
    public void Missing_runtime_is_reported_with_the_download_link_and_nothing_throws()
    {
        using var library = NdiLibrary.Load(_ => null, _ => false);

        Assert.False(library.IsAvailable);
        Assert.Contains("https://ndi.video/tools/", library.UnavailableReason);
        Assert.Throws<InvalidOperationException>(() => library.CreateFinder([]));
        Assert.Throws<InvalidOperationException>(() => library.CreateReceiver(new NdiSourceRef("x", null), "t"));
    }

    [Fact]
    public void The_runtime_loads_dynamically_and_reports_its_version()
    {
        using var library = LoadOrSkip();

        Assert.False(string.IsNullOrWhiteSpace(library.Version));
        Assert.NotNull(library.LoadedFrom);
        Assert.NotEqual(IntPtr.Zero, library.GetExport("NDIlib_recv_capture_v2"));
    }

    [Fact]
    public void A_test_pattern_sent_through_the_runtime_arrives_with_the_right_size_and_colors()
    {
        using var library = LoadOrSkip();
        var sourceName = "IBT Test " + Guid.NewGuid().ToString("N")[..6];

        var create = (delegate* unmanaged[Cdecl]<SendCreate*, IntPtr>)library.GetExport("NDIlib_send_create");
        var send = (delegate* unmanaged[Cdecl]<IntPtr, VideoFrame*, void>)library.GetExport("NDIlib_send_send_video_v2");
        var destroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)library.GetExport("NDIlib_send_destroy");
        Assert.True(create != null && send != null && destroy != null);

        var namePtr = Marshal.StringToCoTaskMemUTF8(sourceName);
        var settings = new SendCreate { Name = (byte*)namePtr, Groups = null, ClockVideo = 1, ClockAudio = 0 };
        var sender = create(&settings);
        Assert.NotEqual(IntPtr.Zero, sender);

        var pixels = (byte*)NativeMemory.Alloc((nuint)(W * H * 4));
        FillBars(pixels);
        using var stop = new CancellationTokenSource();
        var pump = new Thread(() =>
        {
            var frame = new VideoFrame
            {
                Xres = W,
                Yres = H,
                FourCc = 0x41524742,
                FrameRateN = 30,
                FrameRateD = 1,
                PictureAspectRatio = (float)W / H,
                FrameFormatType = 1, // progressive
                Timecode = long.MaxValue, // synthesize
                Data = pixels,
                LineStrideInBytes = W * 4,
            };
            while (!stop.IsCancellationRequested)
            {
                send(sender, &frame);
            }
        }) { IsBackground = true };
        pump.Start();

        try
        {
            using var finder = library.CreateFinder([]);
            NdiSourceInfo? found = null;
            var deadline = Environment.TickCount64 + 15000;
            while (found is null && Environment.TickCount64 < deadline)
            {
                finder.WaitForChange(500);
                found = finder.GetSources().FirstOrDefault(s => s.Name.Contains(sourceName, StringComparison.Ordinal));
            }

            Assert.NotNull(found);

            using var receiver = new NdiReceiver(library, "IBT Test receiver");
            var captured = false;
            var width = 0;
            var height = 0;
            var samples = new (byte B, byte G, byte R)[Bars.Length];
            receiver.Frame += (in NdiFrameView frame) =>
            {
                if (captured)
                {
                    return;
                }

                width = frame.Width;
                height = frame.Height;
                for (var i = 0; i < Bars.Length; i++)
                {
                    var x = (int)((i + 0.5) * frame.Width / Bars.Length);
                    var o = 540 * frame.Stride + x * 4;
                    samples[i] = (frame.Data[o], frame.Data[o + 1], frame.Data[o + 2]);
                }

                captured = true;
            };

            // By name (the finder's own name) first.
            receiver.Connect(new NdiSourceRef(found.Name, null));
            var end = Environment.TickCount64 + 15000;
            while (!captured && Environment.TickCount64 < end)
            {
                Thread.Sleep(20);
            }

            Assert.True(captured, "nenhum quadro chegou");
            Assert.Equal(W, width);
            Assert.Equal(H, height);
            for (var i = 0; i < Bars.Length; i++)
            {
                Assert.InRange(samples[i].B, Math.Max(0, Bars[i].B - 24), Math.Min(255, Bars[i].B + 24));
                Assert.InRange(samples[i].G, Math.Max(0, Bars[i].G - 24), Math.Min(255, Bars[i].G + 24));
                Assert.InRange(samples[i].R, Math.Max(0, Bars[i].R - 24), Math.Min(255, Bars[i].R + 24));
            }

            Assert.Equal(NdiConnectionState.Connected, receiver.State);
            Assert.True(receiver.FramesReceived > 0);
        }
        finally
        {
            stop.Cancel();
            pump.Join(5000);
            destroy(sender);
            NativeMemory.Free(pixels);
            Marshal.FreeCoTaskMem(namePtr);
        }
    }

    private static void FillBars(byte* pixels)
    {
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var bar = Bars[x * Bars.Length / W];
                var o = (y * W + x) * 4;
                pixels[o] = bar.B;
                pixels[o + 1] = bar.G;
                pixels[o + 2] = bar.R;
                pixels[o + 3] = 255;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SendCreate
    {
        public byte* Name;
        public byte* Groups;
        public byte ClockVideo;
        public byte ClockAudio;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VideoFrame
    {
        public int Xres;
        public int Yres;
        public int FourCc;
        public int FrameRateN;
        public int FrameRateD;
        public float PictureAspectRatio;
        public int FrameFormatType;
        public long Timecode;
        public byte* Data;
        public int LineStrideInBytes;
        public byte* Metadata;
        public long Timestamp;
    }
}

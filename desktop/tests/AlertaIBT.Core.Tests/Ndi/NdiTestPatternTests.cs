using AlertaIBT.Core.Ndi;

namespace AlertaIBT.Core.Tests.Ndi;

public sealed class NdiTestPatternTests
{
    private static (byte B, byte G, byte R) At(byte[] buffer, int width, int x, int y)
    {
        var o = (y * width + x) * 4;
        return (buffer[o], buffer[o + 1], buffer[o + 2]);
    }

    [Fact]
    public void Draws_seven_bars_a_ramp_and_a_white_border()
    {
        var pattern = new NdiTestPattern(2560, 768);
        var buffer = new byte[2560 * 768 * 4];

        pattern.Render(buffer, 0);

        for (var i = 0; i < NdiTestPattern.Bars.Length; i++)
        {
            var x = (int)((i + 0.5) * 2560 / NdiTestPattern.Bars.Length);
            Assert.Equal(NdiTestPattern.Bars[i], At(buffer, 2560, x, 300));
        }

        Assert.Equal((255, 255, 255), At(buffer, 2560, 2, 300));
        Assert.Equal((255, 255, 255), At(buffer, 2560, 1200, 2));
        var ramp = At(buffer, 2560, 1280, 700);
        Assert.InRange(ramp.R, 120, 135);
        Assert.Equal(255, buffer[3]); // opaque
    }

    [Fact]
    public void The_marker_moves_and_bounces_inside_the_border()
    {
        var pattern = new NdiTestPattern(2560, 768);
        var buffer = new byte[2560 * 768 * 4];

        var a = pattern.MarkerX(0);
        var b = pattern.MarkerX(1);
        var far = pattern.MarkerX(100);

        Assert.NotEqual(a, b);
        Assert.InRange(far, NdiTestPattern.Border, 2560 - NdiTestPattern.Border - NdiTestPattern.MarkerWidth);
        pattern.Render(buffer, 1);
        Assert.Equal((255, 255, 255), At(buffer, 2560, b + 5, 300));
    }

    [Fact]
    public void Rendering_allocates_nothing()
    {
        var pattern = new NdiTestPattern(1280, 384);
        var buffer = new byte[1280 * 384 * 4];
        pattern.Render(buffer, 0);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 50; i++)
        {
            pattern.Render(buffer, i / 60.0);
        }

        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024);
    }
}

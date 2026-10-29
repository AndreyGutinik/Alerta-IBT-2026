using AlertaIBT.Core.Ndi;

namespace AlertaIBT.Core.Tests.Ndi;

public sealed class NdiCropMathTests
{
    [Fact]
    public void Full_hd_on_the_led_scales_by_four_thirds_and_shows_53_percent()
    {
        var crop = NdiCropMath.Compute(1920, 1080, 2560, 768, 50);

        Assert.Equal(4.0 / 3.0, crop.Scale, 6);
        Assert.Equal(2560, crop.ScaledWidth, 6);
        Assert.Equal(1440, crop.ScaledHeight, 6);
        Assert.Equal(672, crop.HiddenRows, 6);
        Assert.Equal(0, crop.HiddenColumns, 6);
        Assert.Equal(768.0 / 1440.0, crop.VisibleFractionY, 6);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 336)]
    [InlineData(100, 672)]
    public void Vertical_position_picks_the_top_offset(double position, double expectedTop)
    {
        var crop = NdiCropMath.Compute(1920, 1080, 2560, 768, position);

        Assert.Equal(expectedTop, crop.TopOffset, 6);
    }

    [Fact]
    public void Position_zero_shows_the_top_of_the_picture_and_100_the_bottom()
    {
        var top = NdiCropMath.Compute(1920, 1080, 2560, 768, 0);
        var bottom = NdiCropMath.Compute(1920, 1080, 2560, 768, 100);

        Assert.Equal(0, top.V0, 9);
        Assert.Equal(768.0 / 1440.0, top.V1, 9);
        Assert.Equal(1 - 768.0 / 1440.0, bottom.V0, 9);
        Assert.Equal(1, bottom.V1, 9);
        Assert.Equal(0, top.U0, 9);
        Assert.Equal(1, top.U1, 9);
    }

    [Theory]
    [InlineData(-30, 0)]
    [InlineData(250, 672)]
    public void Position_is_held_in_range(double position, double expectedTop)
    {
        Assert.Equal(expectedTop, NdiCropMath.Compute(1920, 1080, 2560, 768, position).TopOffset, 6);
    }

    [Fact]
    public void Nan_position_means_the_middle()
    {
        Assert.Equal(336, NdiCropMath.Compute(1920, 1080, 2560, 768, double.NaN).TopOffset, 6);
    }

    [Fact]
    public void Hd_720_gives_the_same_picture_as_full_hd()
    {
        var hd = NdiCropMath.Compute(1280, 720, 2560, 768, 30);
        var fhd = NdiCropMath.Compute(1920, 1080, 2560, 768, 30);

        Assert.Equal(2.0, hd.Scale, 6);
        Assert.Equal(fhd.V0, hd.V0, 9);
        Assert.Equal(fhd.V1, hd.V1, 9);
    }

    [Fact]
    public void Uhd_4k_shrinks_by_two_thirds_and_keeps_the_same_visible_share()
    {
        var crop = NdiCropMath.Compute(3840, 2160, 2560, 768, 100);

        Assert.Equal(2.0 / 3.0, crop.Scale, 6);
        Assert.Equal(672, crop.HiddenRows, 6);
        Assert.Equal(768.0 / 1440.0, crop.VisibleFractionY, 6);
    }

    [Fact]
    public void Sixteen_by_ten_hides_more_rows()
    {
        var crop = NdiCropMath.Compute(1920, 1200, 2560, 768, 50);

        Assert.Equal(4.0 / 3.0, crop.Scale, 6);
        Assert.Equal(1600, crop.ScaledHeight, 6);
        Assert.Equal(832, crop.HiddenRows, 6);
        Assert.Equal(416, crop.TopOffset, 6);
    }

    [Fact]
    public void A_different_monitor_size_works()
    {
        var crop = NdiCropMath.Compute(1920, 1080, 1920, 1080, 50);

        Assert.Equal(1, crop.Scale, 9);
        Assert.Equal(0, crop.HiddenRows, 9);
        Assert.Equal(0, crop.V0, 9);
        Assert.Equal(1, crop.V1, 9);

        var tall = NdiCropMath.Compute(1920, 1080, 1080, 1920, 50);
        Assert.Equal(1920.0 / 1080.0, tall.Scale, 6);
        Assert.Equal(0, tall.HiddenRows, 6); // overflows sideways, centered
        Assert.True(tall.HiddenColumns > 0);
        Assert.Equal(1 - tall.U0, tall.U1, 9);
    }

    [Fact]
    public void A_very_wide_source_crops_sideways_centered_and_ignores_the_position()
    {
        var a = NdiCropMath.Compute(3840, 1080, 2560, 768, 0);
        var b = NdiCropMath.Compute(3840, 1080, 2560, 768, 100);

        Assert.Equal(0, a.HiddenRows, 6);
        Assert.True(a.HiddenColumns > 0);
        Assert.Equal(a.U0, b.U0, 9);
        Assert.Equal(0, a.V0, 9);
        Assert.Equal(1, a.V1, 9);
    }

    [Fact]
    public void Source_rect_is_in_source_pixels()
    {
        var crop = NdiCropMath.Compute(1920, 1080, 2560, 768, 100);
        var rect = crop.SourceRect(1920, 1080);

        Assert.Equal(0, rect.X, 6);
        Assert.Equal(1920, rect.Width, 6);
        Assert.Equal(576, rect.Height, 6);
        Assert.Equal(504, rect.Y, 6);
    }

    [Theory]
    [InlineData(0, 1080, 2560, 768)]
    [InlineData(1920, 0, 2560, 768)]
    [InlineData(1920, 1080, 0, 768)]
    [InlineData(1920, 1080, 2560, 0)]
    public void Zero_sizes_throw(int sw, int sh, int tw, int th)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NdiCropMath.Compute(sw, sh, tw, th, 50));
    }
}

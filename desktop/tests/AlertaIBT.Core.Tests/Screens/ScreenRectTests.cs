using AlertaIBT.Core.Screens;

namespace AlertaIBT.Core.Tests.Screens;

// The primary-monitor rectangle and its center, with made-up monitor data in physical pixels.
public sealed class ScreenRectTests
{
    private static ScreenInfo Screen(string name, int x, int y, int w, int h, bool primary) => new(string.Empty, name, x, y, w, h, primary);

    [Fact]
    public void The_center_of_a_1920x1080_monitor_at_the_origin_is_960_540()
    {
        var rect = new ScreenRect(0, 0, 1920, 1080);

        Assert.Equal(960, rect.CenterX);
        Assert.Equal(540, rect.CenterY);
        Assert.Equal(1920, rect.Width);
        Assert.Equal(1080, rect.Height);
    }

    [Fact]
    public void An_odd_size_rounds_the_center_down()
    {
        var rect = new ScreenRect(0, 0, 1921, 1081);

        Assert.Equal(960, rect.CenterX);
        Assert.Equal(540, rect.CenterY);
    }

    [Fact]
    public void A_monitor_left_of_the_primary_one_has_negative_coordinates_and_a_correct_center()
    {
        var led = new ScreenRect(-2560, 0, 0, 768);

        Assert.Equal(-1280, led.CenterX);
        Assert.Equal(384, led.CenterY);
        Assert.True(led.Contains(-1, 0));
        Assert.False(led.Contains(0, 0)); // the right edge belongs to the next monitor
    }

    [Fact]
    public void A_monitor_above_the_primary_one_has_a_negative_y_center()
    {
        var above = new ScreenRect(0, -1080, 1920, 0);

        Assert.Equal(960, above.CenterX);
        Assert.Equal(-540, above.CenterY);
    }

    [Fact]
    public void Clamp_gives_the_nearest_pixel_inside_and_leaves_an_inside_point_alone()
    {
        var rect = new ScreenRect(0, 0, 1920, 1080);

        Assert.Equal((0, 500), rect.Clamp(-1200, 500)); // from the LED on the left
        Assert.Equal((1919, 1079), rect.Clamp(5000, 5000));
        Assert.Equal((10, 20), rect.Clamp(10, 20));
    }

    [Fact]
    public void Empty_rectangles_are_recognized()
    {
        Assert.True(new ScreenRect(0, 0, 0, 100).IsEmpty);
        Assert.True(new ScreenRect(10, 10, 5, 50).IsEmpty);
        Assert.False(new ScreenRect(0, 0, 1, 1).IsEmpty);
    }

    [Fact]
    public void The_primary_monitor_is_found_wherever_it_is_in_the_list_and_with_monitors_at_negative_coordinates()
    {
        var screens = new[]
        {
            Screen("LED", -2560, 0, 2560, 768, primary: false),
            Screen("Retorno", 1920, 0, 1280, 720, primary: false),
            Screen("Principal", 0, 0, 1920, 1080, primary: true),
        };

        var rect = PrimaryMonitorRect.From(screens);

        Assert.Equal(new ScreenRect(0, 0, 1920, 1080), rect);
    }

    [Fact]
    public void Mixed_dpi_physical_pixels_are_used_as_they_are_never_scaled()
    {
        // Primary 4K at 150% next to a 1080p at 100%: Windows reports physical pixels, the lock uses exactly those.
        var screens = new[]
        {
            Screen("Principal 4K", 0, 0, 3840, 2160, primary: true),
            Screen("Secundario", 3840, 0, 1920, 1080, primary: false),
        };

        var rect = PrimaryMonitorRect.From(screens)!.Value;

        Assert.Equal(new ScreenRect(0, 0, 3840, 2160), rect);
        Assert.Equal(1920, rect.CenterX);
        Assert.Equal(1080, rect.CenterY);
    }

    [Fact]
    public void After_the_owner_changes_the_primary_monitor_the_new_one_is_found_and_the_old_one_is_negative()
    {
        var screens = new[]
        {
            Screen("Antigo", -1920, 0, 1920, 1080, primary: false),
            Screen("Novo principal", 0, 0, 2560, 1440, primary: true),
        };

        Assert.Equal(new ScreenRect(0, 0, 2560, 1440), PrimaryMonitorRect.From(screens));
    }

    [Fact]
    public void No_primary_or_a_primary_without_size_gives_null()
    {
        Assert.Null(PrimaryMonitorRect.From([]));
        Assert.Null(PrimaryMonitorRect.From([Screen("A", 0, 0, 1920, 1080, primary: false)]));
        Assert.Null(PrimaryMonitorRect.From([Screen("B", 0, 0, 0, 0, primary: true)]));
    }
}

using AlertaIBT.Core.Screens;

namespace AlertaIBT.Core.Tests.Screens;

public class LedSelectorTests
{
    private const string PathA = @"\\?\DISPLAY#LED0001#5&aaaa&0&UID100#{e6f07b5f}";
    private const string PathB = @"\\?\DISPLAY#LED0002#5&bbbb&0&UID200#{e6f07b5f}";

    private static ScreenInfo Led(string path, bool primary = false, int x = 1920) =>
        new(path, @"\\.\DISPLAY2", x, 0, 2560, 768, primary);

    private static readonly ScreenInfo Desk = new(@"\\?\DISPLAY#DELL#1", @"\\.\DISPLAY1", 0, 0, 1920, 1080, true);

    [Fact]
    public void The_only_2560x768_screen_is_the_LED()
    {
        var led = Led(PathA);

        Assert.Same(led, LedSelector.Select([Desk, led], null));
    }

    [Fact]
    public void No_screens_or_no_2560x768_screen_means_no_LED()
    {
        Assert.Null(LedSelector.Select([], null));
        Assert.Null(LedSelector.Select([Desk], null));
        Assert.Null(LedSelector.Select([Desk], PathA));
    }

    [Theory]
    [InlineData(2560, 769)]
    [InlineData(2559, 768)]
    [InlineData(2561, 768)]
    [InlineData(768, 2560)]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void Only_the_exact_resolution_counts(int width, int height)
    {
        var almost = new ScreenInfo(PathA, @"\\.\DISPLAY2", 1920, 0, width, height, false);

        Assert.Null(LedSelector.Select([Desk, almost], null));
    }

    [Fact]
    public void With_two_LEDs_the_last_used_one_wins_wherever_it_is_in_the_list()
    {
        var a = Led(PathA);
        var b = Led(PathB, x: 4480);

        Assert.Same(b, LedSelector.Select([Desk, a, b], PathB));
        Assert.Same(a, LedSelector.Select([Desk, a, b], PathA));
        Assert.Same(b, LedSelector.Select([b, a, Desk], PathB));
    }

    [Fact]
    public void The_last_used_path_is_compared_ignoring_case()
    {
        var a = Led(PathA);
        var b = Led(PathB, x: 4480);

        Assert.Same(b, LedSelector.Select([a, b], PathB.ToLowerInvariant()));
    }

    [Fact]
    public void With_two_LEDs_and_no_known_last_one_the_first_that_is_not_primary_wins()
    {
        var primaryLed = Led(PathA, primary: true, x: 0);
        var b = Led(PathB, x: 2560);
        var c = Led(@"\\?\DISPLAY#LED0003#5&cccc&0&UID300#{e6f07b5f}", x: 5120);

        Assert.Same(b, LedSelector.Select([primaryLed, b, c], null));
        Assert.Same(b, LedSelector.Select([primaryLed, b, c], @"\\?\DISPLAY#GONE#1"));
        Assert.Same(c, LedSelector.Select([c, primaryLed, b], null));
    }

    [Fact]
    public void Without_a_last_one_the_first_non_primary_in_list_order_is_taken()
    {
        var b = Led(PathB);
        var c = Led(@"\\?\DISPLAY#LED0003#5&cccc&0&UID300#{e6f07b5f}", x: 5120);

        Assert.Same(b, LedSelector.Select([b, c], string.Empty));
        Assert.Same(c, LedSelector.Select([c, b], string.Empty));
    }

    [Fact]
    public void A_2560x768_primary_screen_is_used_when_it_is_the_only_one()
    {
        var led = Led(PathA, primary: true, x: 0);

        Assert.Same(led, LedSelector.Select([led], null));
    }

    [Fact]
    public void A_non_primary_LED_is_preferred_over_a_primary_2560x768_when_nothing_else_is_known()
    {
        var primaryLed = Led(PathA, primary: true, x: 0);
        var second = Led(PathB, x: 2560);

        Assert.Same(second, LedSelector.Select([primaryLed, second], null));
    }

    [Fact]
    public void A_blank_last_path_never_matches_screens_without_a_device_path()
    {
        var first = new ScreenInfo(string.Empty, @"\\.\DISPLAY2", 1920, 0, 2560, 768, false);
        var second = new ScreenInfo(string.Empty, @"\\.\DISPLAY3", 4480, 0, 2560, 768, false);

        Assert.Same(first, LedSelector.Select([first, second], string.Empty));
        Assert.Same(first, LedSelector.Select([first, second], "   "));
    }

    [Fact]
    public void A_last_path_that_belongs_to_a_screen_of_another_size_is_ignored()
    {
        var deskWithThePath = Desk with { DevicePath = PathA };
        var led = Led(PathB);

        Assert.Same(led, LedSelector.Select([deskWithThePath, led], PathA));
    }

    [Fact]
    public void When_the_LED_disappears_the_selection_becomes_null_again()
    {
        var led = Led(PathA);

        Assert.Same(led, LedSelector.Select([Desk, led], PathA));
        Assert.Null(LedSelector.Select([Desk], PathA));
    }

    [Fact]
    public void When_one_of_two_LEDs_disappears_the_other_takes_over()
    {
        var a = Led(PathA);
        var b = Led(PathB, x: 4480);

        Assert.Same(a, LedSelector.Select([Desk, a, b], PathA));
        Assert.Same(b, LedSelector.Select([Desk, b], PathA));
    }

    [Fact]
    public void The_list_is_not_changed()
    {
        ScreenInfo[] screens = [Desk, Led(PathB), Led(PathA)];
        var copy = screens.ToArray();

        LedSelector.Select(screens, PathA);

        Assert.Equal(copy, screens);
    }

    [Fact]
    public void A_null_list_is_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(() => LedSelector.Select(null!, null));
    }

    [Fact]
    public void The_screen_info_has_the_shape_of_the_app_flash()
    {
        var info = new ScreenInfo("path", "name", 1, 2, 3, 4, true);

        Assert.Equal(("path", "name", 1, 2, 3, 4, true), (info.DevicePath, info.DeviceName, info.X, info.Y, info.Width, info.Height, info.IsPrimary));
    }
}

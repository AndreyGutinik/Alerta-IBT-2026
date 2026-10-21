using AlertaIBT.Core.Http;

namespace AlertaIBT.Core.Tests.Http;

public class OverlayIntentTests
{
    [Theory]
    [InlineData("GET", "/overlay/off", OverlayIntentKind.Off)]
    [InlineData("POST", "/overlay/off", OverlayIntentKind.Off)]
    [InlineData("GET", "/overlay/on", OverlayIntentKind.On)]
    [InlineData("POST", "/overlay/on", OverlayIntentKind.On)]
    [InlineData("GET", "/overlay/status", OverlayIntentKind.Status)]
    public void The_exact_routes(string method, string url, OverlayIntentKind expected)
    {
        Assert.Equal(expected, OverlayIntent.Parse(method, url));
    }

    [Theory]
    [InlineData("GET", "/overlay/pause", OverlayIntentKind.Off)]
    [InlineData("GET", "/overlay/disable", OverlayIntentKind.Off)]
    [InlineData("GET", "/overlay/resume", OverlayIntentKind.On)]
    [InlineData("GET", "/overlay/enable", OverlayIntentKind.On)]
    [InlineData("POST", "/overlay/pause", OverlayIntentKind.Off)]
    [InlineData("POST", "/overlay/resume", OverlayIntentKind.On)]
    [InlineData("GET", "/overlay?action=off", OverlayIntentKind.Off)]
    [InlineData("GET", "/overlay?action=on", OverlayIntentKind.On)]
    [InlineData("GET", "/overlay/set/on", OverlayIntentKind.On)]
    [InlineData("GET", "/overlay/set/off", OverlayIntentKind.Off)]
    [InlineData("GET", "/overlay/OFF", OverlayIntentKind.Off)]
    [InlineData("GET", "/overlay/Pause", OverlayIntentKind.Off)]
    [InlineData("GET", "/overlay/status?x=1", OverlayIntentKind.Status)]
    public void The_fallback_understands_the_words_Holyrics_may_send(string method, string url, OverlayIntentKind expected)
    {
        Assert.Equal(expected, OverlayIntent.Parse(method, url));
    }

    [Theory]
    [InlineData("GET", "/overlay/off/", OverlayIntentKind.Off)]
    [InlineData("GET", "/overlay/on///", OverlayIntentKind.On)]
    [InlineData("GET", "/overlay/off?token=1", OverlayIntentKind.Off)]
    [InlineData("GET", "/overlay/status/", OverlayIntentKind.Status)]
    public void Trailing_slashes_and_query_strings_do_not_matter_to_the_exact_routes(string method, string url, OverlayIntentKind expected)
    {
        Assert.Equal(expected, OverlayIntent.Parse(method, url));
    }

    [Theory]
    [InlineData("get", "/overlay/off", OverlayIntentKind.Off)]
    [InlineData("post", "/overlay/on", OverlayIntentKind.On)]
    [InlineData("Get", "/overlay/status", OverlayIntentKind.Status)]
    public void The_method_is_case_insensitive(string method, string url, OverlayIntentKind expected)
    {
        Assert.Equal(expected, OverlayIntent.Parse(method, url));
    }

    [Theory]
    [InlineData("GET", "/x")]
    [InlineData("GET", "/")]
    [InlineData("GET", "")]
    [InlineData("GET", "/overlay")]
    [InlineData("GET", "/overlay/")]
    [InlineData("GET", "/overlay/foo")]
    [InlineData("GET", "/overlay/online")]
    [InlineData("GET", "/overlay/offline")]
    [InlineData("GET", "/other/pause")]
    [InlineData("GET", "/status")]
    public void Anything_else_is_not_found(string method, string url)
    {
        Assert.Equal(OverlayIntentKind.NotFound, OverlayIntent.Parse(method, url));
    }

    [Theory]
    [InlineData("PUT", "/overlay/off")]
    [InlineData("DELETE", "/overlay/on")]
    [InlineData("POST", "/overlay/status")]
    [InlineData("OPTIONS", "/overlay/off")]
    [InlineData("HEAD", "/overlay/status")]
    public void The_wrong_method_is_not_found(string method, string url)
    {
        Assert.Equal(OverlayIntentKind.NotFound, OverlayIntent.Parse(method, url));
    }

    [Fact]
    public void An_empty_method_is_a_GET_like_in_the_old_app()
    {
        Assert.Equal(OverlayIntentKind.Status, OverlayIntent.Parse("", "/overlay/status"));
    }

    [Fact]
    public void Off_wins_when_a_url_says_both()
    {
        Assert.Equal(OverlayIntentKind.Off, OverlayIntent.Parse("GET", "/overlay/resume?then=pause"));
    }

    [Fact]
    public void The_exact_routes_are_case_sensitive_like_the_old_app_and_the_fallback_needs_the_overlay_prefix()
    {
        Assert.Equal(OverlayIntentKind.NotFound, OverlayIntent.Parse("GET", "/Overlay/Off"));
    }

    [Fact]
    public void Null_arguments_are_not_found_not_a_crash()
    {
        Assert.Equal(OverlayIntentKind.NotFound, OverlayIntent.Parse(null!, null!));
    }
}

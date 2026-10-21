using AlertaIBT.Core.Services;
using AlertaIBT.Core.Telegram;

namespace AlertaIBT.Core.Tests.Services;

public class StatusHubTests
{
    private readonly StatusHub _hub = new();
    private readonly List<string> _changes = [];

    public StatusHubTests() => _hub.PropertyChanged += (_, e) => _changes.Add(e.PropertyName!);

    // ---- start-up values ----

    [Fact]
    public void Everything_starts_unconfigured_off_or_waiting()
    {
        Assert.Equal(StatusLight.Gray, _hub.Telegram.Light);
        Assert.Equal(TelegramState.NotConfigured, _hub.TelegramState);
        Assert.Equal(StatusLight.Gray, _hub.Site.Light);
        Assert.Equal(StatusLight.Red, _hub.Led.Light);
        Assert.Equal(StatusLight.Gray, _hub.Bar.Light);
        Assert.Equal(StatusLight.Gray, _hub.Gemini.Light);
        Assert.Equal(GeminiState.Unknown, _hub.GeminiState);
        Assert.Equal(StatusLight.Gray, _hub.Http.Light);
        Assert.Equal(AlertPill.LedOff, _hub.Pill);
        Assert.False(_hub.Paused);
        Assert.False(_hub.Showing);
        Assert.Null(_hub.LastError);
    }

    // ---- Telegram ----

    [Theory]
    [InlineData(TelegramState.NotConfigured, StatusLight.Gray)]
    [InlineData(TelegramState.Connecting, StatusLight.Yellow)]
    [InlineData(TelegramState.Online, StatusLight.Green)]
    [InlineData(TelegramState.Reconnecting, StatusLight.Yellow)]
    [InlineData(TelegramState.Offline, StatusLight.Red)]
    [InlineData(TelegramState.Conflict, StatusLight.Red)]
    public void The_telegram_dot_follows_the_connection_state(TelegramState state, StatusLight light)
    {
        _hub.SetTelegram(state);

        Assert.Equal(light, _hub.Telegram.Light);
        Assert.Equal(state, _hub.TelegramState);
    }

    [Fact]
    public void A_conflict_explains_that_the_old_app_may_be_open()
    {
        _hub.SetTelegram(TelegramState.Conflict);

        Assert.Contains("antigo", _hub.Telegram.Detail);
    }

    [Fact]
    public void An_offline_dot_carries_the_error_in_its_detail()
    {
        _hub.SetTelegram(TelegramState.Offline, "sem internet");

        Assert.Equal("sem internet", _hub.Telegram.Detail);
    }

    // ---- LED, bar and pill ----

    [Fact]
    public void The_pill_follows_pause_then_led_then_showing()
    {
        _hub.SetLedPresent(true);
        Assert.Equal(AlertPill.Ready, _hub.Pill);

        _hub.SetShowing(true);
        Assert.Equal(AlertPill.Showing, _hub.Pill);

        _hub.SetLedPresent(false);
        Assert.Equal(AlertPill.LedOff, _hub.Pill);

        _hub.SetPaused(true);
        Assert.Equal(AlertPill.Paused, _hub.Pill); // the sermon outranks everything

        _hub.SetPaused(false);
        _hub.SetLedPresent(true);
        _hub.SetShowing(false);
        Assert.Equal(AlertPill.Ready, _hub.Pill);
    }

    [Fact]
    public void The_bar_dot_is_red_when_capture_exclusion_failed_whatever_else_is_going_on()
    {
        _hub.SetLedPresent(true);
        _hub.SetCaptureExcluded(true);
        Assert.Equal(StatusLight.Green, _hub.Bar.Light);

        _hub.SetCaptureExcluded(false);
        Assert.Equal(StatusLight.Red, _hub.Bar.Light);

        _hub.SetPaused(true);
        Assert.Equal(StatusLight.Red, _hub.Bar.Light);

        _hub.SetCaptureExcluded(true);
        Assert.Equal(StatusLight.Yellow, _hub.Bar.Light); // paused
        _hub.SetPaused(false);
        _hub.SetLedPresent(false);
        Assert.Equal(StatusLight.Gray, _hub.Bar.Light);
    }

    [Fact]
    public void An_unknown_capture_state_is_not_an_alarm()
    {
        _hub.SetLedPresent(true);
        _hub.SetCaptureExcluded(null);

        Assert.Equal(StatusLight.Green, _hub.Bar.Light);
    }

    [Fact]
    public void The_led_dot_shows_the_description()
    {
        _hub.SetLedPresent(true);
        _hub.SetLedDescription("2560x768 em DISPLAY2");

        Assert.Equal(StatusLight.Green, _hub.Led.Light);
        Assert.Equal("2560x768 em DISPLAY2", _hub.Led.Detail);
    }

    // ---- Gemini ----

    [Fact]
    public void Gemini_is_blue_offline_when_it_failed_or_the_user_chose_offline()
    {
        _hub.SetGemini(true);
        Assert.Equal(StatusLight.Green, _hub.Gemini.Light);

        _hub.SetGemini(false);
        Assert.Equal(StatusLight.Blue, _hub.Gemini.Light);
        Assert.Equal("Offline", _hub.Gemini.Text);

        _hub.SetGemini(true);
        _hub.SetCorrectionMode(CorrectionMode.OfflineOnly);
        Assert.Equal(StatusLight.Blue, _hub.Gemini.Light);
        Assert.Equal("Offline", _hub.Gemini.Text);

        _hub.SetCorrectionMode(CorrectionMode.Disabled);
        Assert.Equal(StatusLight.Gray, _hub.Gemini.Light);

        _hub.SetCorrectionMode(CorrectionMode.Gemini);
        Assert.Equal(StatusLight.Green, _hub.Gemini.Light);
    }

    // ---- local API ----

    [Fact]
    public void The_http_dot_tells_listening_busy_and_stopped()
    {
        _hub.SetHttp(HttpApiState.Listening, "127.0.0.1:8787");
        Assert.Equal(StatusLight.Green, _hub.Http.Light);
        Assert.Equal("127.0.0.1:8787", _hub.Http.Detail);

        _hub.SetHttp(HttpApiState.PortBusy, "a porta esta ocupada");
        Assert.Equal(StatusLight.Red, _hub.Http.Light);
        Assert.Equal("a porta esta ocupada", _hub.Http.Detail);

        _hub.SetHttp(HttpApiState.Stopped);
        Assert.Equal(StatusLight.Gray, _hub.Http.Light);
    }

    // ---- change notification ----

    [Fact]
    public void Only_the_properties_that_changed_are_announced_and_a_repeat_is_silent()
    {
        _hub.SetPaused(true);

        Assert.Contains("Paused", _changes);
        Assert.Contains("Pill", _changes);
        Assert.Contains("Bar", _changes);
        Assert.DoesNotContain("Telegram", _changes);
        Assert.DoesNotContain("Gemini", _changes);

        _changes.Clear();
        _hub.SetPaused(true);
        _hub.SetTelegram(TelegramState.NotConfigured);
        Assert.Empty(_changes);
    }

    [Fact]
    public void A_dot_is_announced_when_only_its_detail_changes()
    {
        _hub.SetTelegram(TelegramState.Offline, "um erro");
        _changes.Clear();

        _hub.SetTelegram(TelegramState.Offline, "outro erro");

        Assert.Equal(["Telegram"], _changes);
    }

    [Fact]
    public void A_listener_that_throws_does_not_stop_the_others_or_the_caller()
    {
        var reached = 0;
        _hub.PropertyChanged += (_, _) => throw new InvalidOperationException("bug");
        _hub.PropertyChanged += (_, _) => reached++;

        _hub.SetPaused(true);

        Assert.True(reached > 0);
        Assert.True(_hub.Paused);
    }

    [Fact]
    public async Task Many_threads_reporting_at_once_end_in_a_consistent_state()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(
            () =>
            {
                for (var n = 0; n < 500; n++)
                {
                    _hub.SetShowing(n % 2 == 0);
                    _hub.SetPaused((n + i) % 3 == 0);
                    _hub.SetLedPresent(true);
                }
            },
            TestContext.Current.CancellationToken)));

        _hub.SetPaused(false);
        _hub.SetShowing(false);
        Assert.Equal(AlertPill.Ready, _hub.Pill);
    }

    // ---- errors ----

    [Fact]
    public void ReportError_keeps_the_last_error_with_its_source_and_time()
    {
        var at = new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

        _hub.ReportError("Telegram", new InvalidOperationException("boom"), at);

        Assert.Equal("Telegram: boom", _hub.LastError);
        Assert.Equal(at, _hub.LastErrorAt);
        Assert.Contains("LastError", _changes);
    }
}

using System.Text.Json.Nodes;
using AlertaIBT.Core.Overlay;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Tests.Services;

namespace AlertaIBT.Core.Tests.Firebase;

// /live/now.startedAt and .durationMs: the real start and length of the pass on the bar (what the overlay controller reports).
public sealed class SiteEnginePassTimingTests
{
    private const string Alert = "fiat uno azul pcp1a11";

    // Stands for the overlay controller: it tells the engine what the bar is doing, the way OverlayDisplay does.
    private sealed class FakePassClock : IPassClock
    {
        private PassTiming? _current;

        public event Action? Changed;

        public PassTiming? Current => Volatile.Read(ref _current);

        public void Begin(string text, long startedAtMs, TimeSpan total)
        {
            Volatile.Write(ref _current, new PassTiming(text, startedAtMs, (long)Math.Round(total.TotalMilliseconds)));
            Changed?.Invoke();
        }

        public void End()
        {
            Volatile.Write(ref _current, null);
            Changed?.Invoke();
        }
    }

    // What the controller computes for an alert: the real text width and the speed of the bar settings.
    private static TimeSpan TimelineTotal(double textWidth, int speed = 90, int slideMs = 400) =>
        new BarTimeline(2560, textWidth, speed, TimeSpan.FromMilliseconds(slideMs)).Total;

    private static JsonNode? Now(SiteRig rig) => rig.Live?["now"];

    [Fact]
    public async Task The_site_gets_the_start_and_the_length_the_bar_really_has()
    {
        var clock = new FakePassClock();
        await using var rig = new SiteRig(passClock: clock);
        await rig.StartOnlineAsync();
        _ = await rig.H.Pc(Alert);
        var pass = await rig.H.Display.NextPass(1);

        var total = TimelineTotal(1337.5);
        var startedAt = rig.NowMs + 5;
        clock.Begin(pass.Text, startedAt, total);

        await rig.WaitFor(() => Now(rig)?["durationMs"] is not null, "duration in live");
        Assert.Equal((long)Math.Round(total.TotalMilliseconds), Now(rig)!["durationMs"]!.GetValue<long>());
        Assert.Equal(startedAt, Now(rig)!["startedAt"]!.GetValue<long>());
        Assert.Equal("pc", Now(rig)!["source"]!.GetValue<string>());
    }

    [Fact]
    public async Task Without_a_pass_clock_startedAt_stays_and_there_is_no_duration()
    {
        await using var rig = new SiteRig();
        await rig.StartOnlineAsync();
        _ = await rig.H.Pc(Alert);
        _ = await rig.H.Display.NextPass(1);

        await rig.WaitFor(() => Now(rig) is not null, "now in live");
        Assert.Null(Now(rig)!["durationMs"]);
        Assert.True(Now(rig)!["startedAt"]!.GetValue<long>() > 0);
    }

    [Fact]
    public async Task A_pass_of_another_text_is_not_published_for_the_current_item()
    {
        var clock = new FakePassClock();
        await using var rig = new SiteRig(passClock: clock);
        await rig.StartOnlineAsync();
        clock.Begin("some other text", 123, TimeSpan.FromSeconds(30));
        _ = await rig.H.Pc(Alert);
        _ = await rig.H.Display.NextPass(1);

        await rig.WaitFor(() => Now(rig) is not null, "now in live");
        await Wait.Settle();
        Assert.Null(Now(rig)!["durationMs"]);
        Assert.NotEqual(123, Now(rig)!["startedAt"]!.GetValue<long>());
    }

    [Fact]
    public async Task When_the_led_returns_mid_scroll_the_pass_restarts_with_a_new_start()
    {
        var clock = new FakePassClock();
        await using var rig = new SiteRig(passClock: clock);
        await rig.StartOnlineAsync();
        _ = await rig.H.Pc(Alert);
        var first = await rig.H.Display.NextPass(1);
        var total = TimelineTotal(900);
        var firstStart = rig.NowMs;
        clock.Begin(first.Text, firstStart, total);
        await rig.WaitFor(() => Now(rig)?["startedAt"]?.GetValue<long>() == firstStart, "first start in live");

        // The LED vanishes: the pass is cut short, the bar hides, the item goes back to the front.
        rig.H.Display.SetLed(false);
        clock.End();
        await rig.WaitFor(() => Now(rig) is null, "now cleared while the LED is away");

        rig.Clock.Advance(TimeSpan.FromSeconds(20));
        rig.H.Display.SetLed(true);
        var second = await rig.H.Display.NextPass(2);
        var secondStart = rig.NowMs;
        clock.Begin(second.Text, secondStart, total);

        await rig.WaitFor(() => Now(rig)?["startedAt"]?.GetValue<long>() == secondStart, "restart in live");
        Assert.NotEqual(firstStart, secondStart);
        Assert.Equal((long)Math.Round(total.TotalMilliseconds), Now(rig)!["durationMs"]!.GetValue<long>());
    }

    [Fact]
    public async Task The_pass_ending_or_the_pause_clears_now_and_the_duration()
    {
        var clock = new FakePassClock();
        await using var rig = new SiteRig(passClock: clock);
        await rig.StartOnlineAsync();
        _ = await rig.H.Pc(Alert);
        var pass = await rig.H.Display.NextPass(1);
        clock.Begin(pass.Text, rig.NowMs, TimelineTotal(500));
        await rig.WaitFor(() => Now(rig)?["durationMs"] is not null, "duration in live");

        rig.H.Service.Pause(); // the sermon: cancels the pass, the bar hides
        clock.End();
        await rig.WaitFor(() => Now(rig) is null, "now cleared by the pause");

        rig.H.Service.Resume();
        _ = await rig.H.Pc("hilux preta xyz9k88");
        var next = await rig.H.Display.NextPass(2);
        clock.Begin(next.Text, rig.NowMs, TimelineTotal(700));
        await rig.WaitFor(() => Now(rig)?["durationMs"] is not null, "duration of the next pass");

        next.Complete(true);
        clock.End();
        await rig.WaitFor(() => Now(rig) is null, "now cleared at the end of the pass");
    }

    [Fact]
    public async Task A_change_of_the_bar_settings_is_published_soon_and_the_next_pass_uses_the_new_speed()
    {
        var clock = new FakePassClock();
        await using var rig = new SiteRig(passClock: clock);
        await rig.StartOnlineAsync();
        await rig.WaitFor(() => rig.Live?["bar"] is not null, "live published");
        Assert.Equal(90, rig.Live!["bar"]!["speed"]!.GetValue<int>());

        rig.H.Settings = new AppSettings { Bar = new BarSettings { Speed = 200, Height = 70 } };
        rig.Engine.SettingsChanged();
        await rig.WaitFor(() => rig.Live?["bar"]?["speed"]?.GetValue<int>() == 200, "new speed in live");
        Assert.Equal(70, rig.Live!["bar"]!["height"]!.GetValue<int>());

        _ = await rig.H.Pc(Alert);
        var pass = await rig.H.Display.NextPass(1);
        var total = TimelineTotal(800, speed: 200);
        clock.Begin(pass.Text, rig.NowMs, total);
        await rig.WaitFor(() => Now(rig)?["durationMs"] is not null, "duration in live");
        Assert.Equal((long)Math.Round(total.TotalMilliseconds), Now(rig)!["durationMs"]!.GetValue<long>());
        Assert.True(total < TimelineTotal(800, speed: 90));
    }
}

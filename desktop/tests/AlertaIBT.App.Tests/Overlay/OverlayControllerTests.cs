using AlertaIBT.App.Overlay;
using AlertaIBT.Core.Overlay;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.App.Tests.Overlay;

public sealed class OverlayControllerTests : IDisposable
{
    private static readonly ScreenInfo Led = new("led", @"\\.\DISPLAY2", 1920, 0, 2560, 768, false);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-ctl-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new();
    private readonly List<FakeSurface> _surfaces = [];
    private readonly FakeFrames _frames = new();
    private readonly FakeLed _led = new(Led);
    private readonly QueueUi _ui = new();
    private readonly SettingsService _settings;
    private readonly OverlayController _controller;

    public OverlayControllerTests()
    {
        _settings = new SettingsService(new SettingsStore(Path.Combine(_dir, "settings.json")), _clock);

        // 2560 px of screen + 1000 px of text at 100 px/s = 35.6 s of scroll, plus two slides of 320 ms.
        _settings.Update(s => s with { Bar = s.Bar with { Speed = 100 } });
        _controller = new OverlayController(_settings, _led, NewSurface, _frames, _ui, NullLogger<OverlayController>.Instance, _clock);
    }

    public void Dispose()
    {
        _controller.Dispose();
        _settings.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private FakeSurface Surface => _surfaces[^1];

    private static TimeSpan Total => new BarTimeline(2560, 1000, 100, TimeSpan.FromMilliseconds(320)).Total;

    private FakeSurface NewSurface()
    {
        var surface = new FakeSurface();
        _surfaces.Add(surface);
        return surface;
    }

    [Fact]
    public async Task A_pass_ends_true_when_the_text_has_crossed_the_screen_and_the_bar_is_hidden()
    {
        var task = _controller.ShowAsync("CRETA PRETO | PLACA: GBR-9003", CancellationToken.None);
        Assert.True(_frames.Running);
        Assert.True(_controller.IsShowing);

        var origin = TimeSpan.FromSeconds(1000);
        _frames.Push(origin);
        _frames.Push(origin + TimeSpan.FromSeconds(10));
        Assert.False(task.IsCompleted);
        _frames.Push(origin + Total + TimeSpan.FromMilliseconds(16));

        Assert.True(await task.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.False(_frames.Running);
        Assert.False(_controller.IsShowing);
        Assert.Equal(1, Surface.HideCount);
        Assert.Equal(BarPhase.Done, Surface.Frames[^1].Phase);
    }

    [Fact]
    public async Task The_progress_of_the_pass_grows_from_zero_to_one_and_is_null_when_nothing_is_on_the_bar()
    {
        Assert.Null(_controller.PassProgress);

        var task = _controller.ShowAsync("CRETA PRETO | PLACA: GBR-9003", CancellationToken.None);
        Assert.Equal(0, _controller.PassProgress); // on the bar, no frame yet
        var origin = TimeSpan.FromSeconds(1000);
        _frames.Push(origin);
        Assert.Equal(0, _controller.PassProgress);
        _frames.Push(origin + Total / 2);
        Assert.Equal(0.5, _controller.PassProgress!.Value, 3);
        _frames.Push(origin + Total + TimeSpan.FromMilliseconds(16));
        await task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Null(_controller.PassProgress);
    }

    [Fact]
    public void The_three_second_test_of_the_bar_does_not_report_progress()
    {
        _ = _controller.PreviewAsync();
        _frames.Push(TimeSpan.FromSeconds(500));
        _frames.Push(TimeSpan.FromSeconds(501));

        Assert.True(_controller.IsShowing);
        Assert.Null(_controller.PassProgress);
    }

    [Fact]
    public void The_text_and_the_current_settings_and_the_led_go_to_the_surface()
    {
        _settings.Update(s => s with { Bar = s.Bar with { Height = 80, Position = "bottom" } });

        _ = _controller.ShowAsync("GOL BRANCO", CancellationToken.None);

        var (text, bar, screen) = Assert.Single(Surface.Prepared);
        Assert.Equal("GOL BRANCO", text);
        Assert.Equal(80, bar.Height);
        Assert.Equal("bottom", bar.Position);
        Assert.Equal(Led, screen);
    }

    [Fact]
    public async Task Time_comes_from_the_frame_clock_so_dropped_frames_do_not_slow_the_text()
    {
        var task = _controller.ShowAsync("X", CancellationToken.None);
        var origin = TimeSpan.FromSeconds(50);

        // Only three frames in 10 seconds: the text is still exactly where speed times time says.
        _frames.Push(origin);
        _frames.Push(origin + TimeSpan.FromSeconds(5));
        _frames.Push(origin + TimeSpan.FromSeconds(10));

        var expectedX = 2560 - (100 * (10 - 0.32));
        Assert.Equal(expectedX, Surface.Frames[^1].TextX, 6);
        Assert.Equal(BarPhase.Scroll, Surface.Frames[^1].Phase);
        await _controller.DisposeAndWaitAsync(task);
    }

    [Fact]
    public void The_first_frame_is_time_zero_whatever_the_frame_clock_reads()
    {
        _ = _controller.ShowAsync("X", CancellationToken.None);

        _frames.Push(TimeSpan.FromHours(7));

        var first = Surface.Frames[0];
        Assert.Equal(BarPhase.In, first.Phase);
        Assert.Equal(1, first.Offset);
        Assert.Equal(0, first.Opacity);
        Assert.Equal(2560, first.TextX);
    }

    [Fact]
    public async Task Without_a_led_the_alert_does_not_show_and_the_result_is_false()
    {
        _led.Led = null;

        var passed = await _controller.ShowAsync("X", CancellationToken.None);

        Assert.False(passed);
        Assert.Empty(_surfaces.SelectMany(s => s.Prepared));
        Assert.False(_frames.Running);
    }

    [Fact]
    public async Task Cancelling_ends_false_and_hides_the_bar()
    {
        using var cts = new CancellationTokenSource();
        var task = _controller.ShowAsync("X", cts.Token);
        _frames.Push(TimeSpan.FromSeconds(1));

        await cts.CancelAsync();
        _ui.Drain();

        Assert.False(await task.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Equal(1, Surface.HideCount);
        Assert.False(_frames.Running);
        Assert.False(_controller.IsShowing);
    }

    [Fact]
    public async Task A_token_already_cancelled_shows_nothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Assert.False(await _controller.ShowAsync("X", cts.Token));
        Assert.Empty(_surfaces.SelectMany(s => s.Prepared));
    }

    [Fact]
    public async Task The_led_vanishing_mid_scroll_ends_false_and_hides_the_bar()
    {
        var task = _controller.ShowAsync("X", CancellationToken.None);
        _frames.Push(TimeSpan.FromSeconds(1));
        _frames.Push(TimeSpan.FromSeconds(8));

        _led.Raise(null); // the resolution changed: no 2560x768 screen any more
        _ui.Drain();

        Assert.False(await task.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Equal(1, Surface.HideCount);
        Assert.False(_frames.Running);
    }

    [Fact]
    public async Task The_led_moving_to_another_place_also_cuts_the_pass_so_it_can_replay_there()
    {
        var task = _controller.ShowAsync("X", CancellationToken.None);
        _frames.Push(TimeSpan.FromSeconds(1));

        _led.Raise(Led with { X = -2560 });
        _ui.Drain();

        Assert.False(await task.WaitAsync(Wait, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_led_change_message_for_the_same_screen_does_not_interrupt()
    {
        var task = _controller.ShowAsync("X", CancellationToken.None);
        _frames.Push(TimeSpan.FromSeconds(1));

        _led.Raise(Led with { });
        _ui.Drain();

        Assert.False(task.IsCompleted);
        Assert.True(_controller.IsShowing);
        await _controller.DisposeAndWaitAsync(task);
    }

    [Fact]
    public async Task After_a_cut_pass_the_next_alert_starts_from_zero()
    {
        var first = _controller.ShowAsync("A", CancellationToken.None);
        _frames.Push(TimeSpan.FromSeconds(1));
        _frames.Push(TimeSpan.FromSeconds(9));
        _led.Raise(null);
        _ui.Drain();
        Assert.False(await first.WaitAsync(Wait, TestContext.Current.CancellationToken));

        _led.Raise(Led);
        _ui.Drain();
        var second = _controller.ShowAsync("A", CancellationToken.None);
        _frames.Push(TimeSpan.FromSeconds(100));

        Assert.Equal(BarPhase.In, Surface.Frames[^1].Phase);
        Assert.Equal(2560, Surface.Frames[^1].TextX);
        await _controller.DisposeAndWaitAsync(second);
    }

    [Fact]
    public async Task Frames_that_stop_coming_end_the_pass_false_after_the_deadline()
    {
        var task = _controller.ShowAsync("X", CancellationToken.None);
        _frames.Push(TimeSpan.FromSeconds(1));

        _clock.Advance(Total + OverlayController.WatchdogMargin - TimeSpan.FromSeconds(1));
        _ui.Drain();
        Assert.False(task.IsCompleted);

        _clock.Advance(TimeSpan.FromSeconds(2));
        _ui.Drain();

        Assert.False(await task.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Equal(1, Surface.HideCount);
    }

    [Fact]
    public async Task A_finished_pass_stops_the_watchdog()
    {
        var task = _controller.ShowAsync("X", CancellationToken.None);
        _frames.Push(TimeSpan.Zero);
        _frames.Push(Total + TimeSpan.FromSeconds(1));
        Assert.True(await task.WaitAsync(Wait, TestContext.Current.CancellationToken));

        _clock.Advance(TimeSpan.FromHours(1));
        _ui.Drain();

        Assert.Equal(1, Surface.HideCount);
    }

    [Fact]
    public async Task A_second_alert_while_one_is_on_the_bar_is_refused()
    {
        var task = _controller.ShowAsync("A", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _controller.ShowAsync("B", CancellationToken.None));

        Assert.True(_controller.IsShowing);
        await _controller.DisposeAndWaitAsync(task);
    }

    [Fact]
    public async Task Empty_text_shows_nothing_and_counts_as_passed()
    {
        Assert.True(await _controller.ShowAsync("   ", CancellationToken.None));
        Assert.Empty(_surfaces.SelectMany(s => s.Prepared));
    }

    [Fact]
    public async Task A_surface_that_fails_to_prepare_is_replaced_and_the_alert_comes_back_false()
    {
        _controller.Start();
        Surface.ThrowOnPrepare = new InvalidOperationException("window gone");
        var broken = Surface;

        Assert.False(await _controller.ShowAsync("X", CancellationToken.None));
        Assert.True(broken.Disposed);

        // The next alert gets a fresh window and works.
        var task = _controller.ShowAsync("X", CancellationToken.None);
        Assert.NotSame(broken, Surface);
        Assert.Single(Surface.Prepared);
        await _controller.DisposeAndWaitAsync(task);
    }

    [Fact]
    public async Task A_surface_that_fails_while_drawing_ends_the_pass_false_and_is_replaced()
    {
        var task = _controller.ShowAsync("X", CancellationToken.None);
        var broken = Surface;
        broken.ThrowOnApply = new InvalidOperationException("device lost");

        _frames.Push(TimeSpan.FromSeconds(1));

        Assert.False(await task.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.True(broken.Disposed);
        Assert.False(_frames.Running);

        var next = _controller.ShowAsync("X", CancellationToken.None);
        Assert.NotSame(broken, Surface);
        await _controller.DisposeAndWaitAsync(next);
    }

    [Fact]
    public void The_window_is_created_at_start_and_capture_exclusion_is_reported()
    {
        var changes = 0;
        _controller.CaptureExcludedChanged += () => changes++;
        Assert.False(_controller.CaptureExcluded);

        _controller.Start();

        Assert.Single(_surfaces);
        Assert.True(_controller.CaptureExcluded);
        Assert.Equal(1, changes);

        Surface.ChangeCapture(false);
        Assert.False(_controller.CaptureExcluded);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void A_surface_that_could_not_exclude_itself_from_capture_reads_false()
    {
        var failing = new FakeSurface { CaptureExcluded = false };
        using var controller = new OverlayController(_settings, _led, () => failing, _frames, _ui, NullLogger<OverlayController>.Instance, _clock);

        controller.Start();

        Assert.False(controller.CaptureExcluded);
    }

    [Fact]
    public async Task The_preview_uses_the_sample_text_and_lasts_three_seconds_whatever_the_speed_setting()
    {
        var task = _controller.PreviewAsync();
        var (text, _, _) = Assert.Single(Surface.Prepared);
        Assert.Equal(BarPreview.SampleText, text);

        var timeline = new BarTimeline(2560, 1000, BarPreview.SpeedFor(2560, 1000, TimeSpan.FromMilliseconds(320), BarPreview.Duration), TimeSpan.FromMilliseconds(320));
        _frames.Push(TimeSpan.Zero);
        _frames.Push(timeline.Total - TimeSpan.FromMilliseconds(1));
        Assert.False(task.IsCompleted);
        _frames.Push(timeline.Total);

        await task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Equal(3.0, timeline.Total.TotalSeconds, 3);
        Assert.Equal(1, Surface.HideCount);
    }

    [Fact]
    public async Task The_preview_is_skipped_while_an_alert_is_on_the_bar()
    {
        var alert = _controller.ShowAsync("A", CancellationToken.None);

        await _controller.PreviewAsync();

        Assert.Single(Surface.Prepared); // only the alert
        Assert.False(alert.IsCompleted);
        await _controller.DisposeAndWaitAsync(alert);
    }

    [Fact]
    public async Task An_alert_cuts_the_preview_short()
    {
        var preview = _controller.PreviewAsync();
        _frames.Push(TimeSpan.FromSeconds(1));

        var alert = _controller.ShowAsync("A", CancellationToken.None);

        await preview.WaitAsync(Wait, TestContext.Current.CancellationToken); // the preview just ends
        Assert.Equal(["EXAMPLE", "A"], Surface.Prepared.Select(p => p.Text == BarPreview.SampleText ? "EXAMPLE" : p.Text));
        Assert.True(_controller.IsShowing);
        Assert.False(alert.IsCompleted);
        await _controller.DisposeAndWaitAsync(alert);
    }

    [Fact]
    public async Task Disposing_ends_the_pass_in_progress()
    {
        var task = _controller.ShowAsync("A", CancellationToken.None);

        _controller.Dispose();

        Assert.False(await task.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.True(Surface.Disposed);
    }
}

internal static class ControllerTestExtensions
{
    // Ends a pass a test left running, so no task is left dangling.
    public static async Task DisposeAndWaitAsync(this OverlayController controller, Task<bool> task)
    {
        controller.Dispose();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

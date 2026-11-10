using System.ComponentModel;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Overlay;
using AlertaIBT.Core.Tests.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Diagnostics;

// Batch 42: the diagnostics of the stability pass, all on fake clocks and fake state: the 1816 classifier and its episodes, the session state, the quit guard, the
// breadcrumb coalescing and the rule "do not move a window that is already there".
public sealed class StabilityDiagnosticsTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 9, 14, 0, 0, TimeSpan.Zero));

    // A Win32Exception that says where it was thrown (the real stack of the 2026-11-06 log).
    private sealed class FakeStackWin32(int code, string stack) : Win32Exception(code)
    {
        public override string? StackTrace => stack;
    }

    private const string HwndStack = "   at System.Windows.Interop.HwndTarget.UpdateWindowSettings(Boolean enableRenderTarget, Nullable`1 channelSet)\n   at System.Windows.Interop.HwndTarget.UpdateWindowPos(IntPtr lParam)";

    // ---- the classifier ----

    [Fact]
    public void The_quota_error_of_the_window_system_from_HwndTarget_is_a_known_failure()
    {
        var error = new FakeStackWin32(1816, HwndStack);

        Assert.True(KnownUiFailures.IsWindowSystemQuota(error));
        Assert.NotNull(KnownUiFailures.Name(error));
    }

    [Fact]
    public void The_same_code_from_somewhere_else_or_another_code_from_HwndTarget_is_not()
    {
        Assert.False(KnownUiFailures.IsWindowSystemQuota(new FakeStackWin32(1816, "   at MyApp.Thing.Do()")));
        Assert.False(KnownUiFailures.IsWindowSystemQuota(new FakeStackWin32(5, HwndStack)));
        Assert.False(KnownUiFailures.IsWindowSystemQuota(new InvalidOperationException("x")));
        Assert.False(KnownUiFailures.IsWindowSystemQuota(null));
        Assert.Null(KnownUiFailures.Name(new InvalidOperationException("x")));
    }

    [Fact]
    public void The_failure_is_found_inside_a_wrapper_exception()
    {
        var wrapped = new InvalidOperationException("dispatcher", new FakeStackWin32(1816, HwndStack));

        Assert.True(KnownUiFailures.IsWindowSystemQuota(wrapped));
    }

    // ---- the episodes ----

    [Fact]
    public void A_flood_of_the_same_failure_is_one_episode_with_one_first_note_and_a_count()
    {
        var episodes = new UiFailureEpisodes(_clock);

        var first = episodes.Record("quota");
        Assert.True(first.IsFirst);
        for (var i = 0; i < 16; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(100));
            Assert.False(episodes.Record("quota").IsFirst);
        }

        Assert.True(episodes.Active);
        Assert.Null(episodes.Poll()); // still inside the episode
    }

    [Fact]
    public void The_episode_ends_after_a_quiet_spell_with_its_count_and_its_duration_once()
    {
        var episodes = new UiFailureEpisodes(_clock);
        for (var i = 0; i < 17; i++)
        {
            episodes.Record("quota");
            if (i < 16)
            {
                _clock.Advance(TimeSpan.FromSeconds(1));
            }
        }

        _clock.Advance(UiFailureEpisodes.QuietToEnd);
        var ended = episodes.Poll();

        Assert.NotNull(ended);
        Assert.Equal(17, ended!.Count);
        Assert.Equal("quota", ended.Kind);
        Assert.Equal(16, ended.Duration.TotalSeconds, 0.5);
        Assert.False(episodes.Active);
        Assert.Null(episodes.Poll()); // told once
    }

    [Fact]
    public void A_new_failure_after_the_end_opens_a_new_episode_with_its_own_first_note()
    {
        var episodes = new UiFailureEpisodes(_clock);
        episodes.Record("quota");
        _clock.Advance(UiFailureEpisodes.QuietToEnd);
        Assert.NotNull(episodes.Poll());

        var again = episodes.Record("quota");

        Assert.True(again.IsFirst);
        Assert.Equal(1, again.Count);
    }

    // ---- the session state ----

    [Fact]
    public void A_locked_session_a_display_that_is_off_or_a_suspending_PC_pauses_the_window_work_and_coming_back_resumes_it_once()
    {
        var state = new SessionState(_clock);
        var resumed = 0;
        state.WorkResumed += () => resumed++;

        Assert.False(state.ShouldPauseWindowWork);

        state.SetLocked(true);
        Assert.True(state.ShouldPauseWindowWork);
        state.SetDisplay(DisplayPower.Off); // locked AND dark: still one pause
        state.SetLocked(false);
        Assert.True(state.ShouldPauseWindowWork); // the display is still off
        Assert.Equal(0, resumed);

        state.SetDisplay(DisplayPower.On);
        Assert.False(state.ShouldPauseWindowWork);
        Assert.Equal(1, resumed);

        state.Suspending();
        Assert.True(state.ShouldPauseWindowWork);
        state.Resumed();
        Assert.Equal(2, resumed);
        state.DisplayTopologyChanged(); // not a pause: nothing to resume
        Assert.Equal(2, resumed);
    }

    [Fact]
    public void A_dimmed_display_and_a_remote_session_do_not_pause_but_a_disconnect_does()
    {
        var state = new SessionState(_clock);

        state.SetDisplay(DisplayPower.Dimmed);
        state.SetRemote(true);
        Assert.False(state.ShouldPauseWindowWork);

        state.SetDisconnected(true);
        Assert.True(state.ShouldPauseWindowWork);
        state.SetDisconnected(false);
        Assert.False(state.ShouldPauseWindowWork);
    }

    [Fact]
    public void The_description_says_locked_display_remote_and_the_last_resume()
    {
        var state = new SessionState(_clock);
        state.SetDisplay(DisplayPower.Off);
        state.SetLocked(true);
        state.SetRemote(true);
        state.Resumed();
        _clock.Advance(TimeSpan.FromSeconds(95));

        var text = state.Describe();

        Assert.Contains("bloqueada", text, StringComparison.Ordinal);
        Assert.Contains("tela desligada", text, StringComparison.Ordinal);
        Assert.Contains("remota", text, StringComparison.Ordinal);
        Assert.Contains("ultimo retorno da suspensao", text, StringComparison.Ordinal);
        Assert.Contains("ha 95 s", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_listener_that_throws_never_breaks_the_state()
    {
        var state = new SessionState(_clock);
        state.Changed += () => throw new InvalidOperationException("listener");
        state.WorkResumed += () => throw new InvalidOperationException("listener");

        state.SetLocked(true);
        state.SetLocked(false);

        Assert.False(state.ShouldPauseWindowWork);
    }

    // ---- the quit guard ----

    [Fact]
    public void A_quit_that_finishes_in_time_never_fires_the_guard()
    {
        var fired = new List<string>();
        using var guard = new ShutdownGuard(TimeSpan.FromSeconds(15), (step, _) => fired.Add(step), _clock);

        guard.Begin();
        _clock.Advance(TimeSpan.FromSeconds(3));
        guard.Complete();
        _clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Empty(fired);
        Assert.False(guard.Fired);
    }

    [Fact]
    public void A_quit_that_freezes_fires_the_guard_once_with_the_step_it_was_in()
    {
        var fired = new List<(string Step, TimeSpan Took)>();
        using var guard = new ShutdownGuard(TimeSpan.FromSeconds(15), (step, took) => fired.Add((step, took)), _clock);
        ShutdownSteps.Run("passo que trava", () => { }); // the last step the app entered

        guard.Begin();
        _clock.Advance(TimeSpan.FromSeconds(14));
        Assert.Empty(fired);
        _clock.Advance(TimeSpan.FromSeconds(2));
        _clock.Advance(TimeSpan.FromMinutes(1)); // not again

        var one = Assert.Single(fired);
        Assert.Equal("passo que trava", one.Step);
        Assert.InRange(one.Took.TotalSeconds, 15, 16.1);
        Assert.True(guard.Fired);
    }

    [Fact]
    public void A_bounded_step_that_never_returns_is_given_up_and_the_next_steps_still_run()
    {
        var log = new CapturingLogger();
        ShutdownSteps.UseLogger(log);
        using var never = new ManualResetEventSlim();
        var ran = new List<string>();

        var finished = ShutdownSteps.RunBounded("chamada do Windows que nao volta", () => never.Wait(), TimeSpan.FromMilliseconds(150));
        ShutdownSteps.Run("proximo passo", () => ran.Add("proximo"));
        never.Set(); // let the helper thread end

        Assert.False(finished);
        Assert.Equal(["proximo"], ran);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("chamada do Windows que nao volta", StringComparison.Ordinal));
        ShutdownSteps.UseLogger(null);
    }

    [Fact]
    public void A_failing_step_is_logged_and_does_not_stop_the_quit()
    {
        var log = new CapturingLogger();
        ShutdownSteps.UseLogger(log);
        var ran = false;

        ShutdownSteps.Run("passo que falha", () => throw new InvalidOperationException("boom"));
        ShutdownSteps.Run("passo seguinte", () => ran = true);

        Assert.True(ran);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("passo que falha", StringComparison.Ordinal));
        ShutdownSteps.UseLogger(null);
    }

    [Fact]
    public void A_bounded_step_that_finishes_reports_true()
    {
        var done = false;

        var finished = ShutdownSteps.RunBounded("passo rapido", () => done = true, TimeSpan.FromSeconds(5));

        Assert.True(finished);
        Assert.True(done);
    }

    // ---- never move a window that is already there ----

    [Fact]
    public void A_window_already_at_the_wanted_rectangle_is_not_moved()
    {
        var wanted = new PixelRect(0, -768, 2560, 56);

        Assert.False(PlacementGuard.NeedsMove(wanted, wanted));
        Assert.True(PlacementGuard.NeedsMove(wanted with { Y = -700 }, wanted));
        Assert.True(PlacementGuard.NeedsMove(wanted with { Width = 1920 }, wanted));
        Assert.True(PlacementGuard.NeedsMove(null, wanted)); // unreadable: place it
    }
}

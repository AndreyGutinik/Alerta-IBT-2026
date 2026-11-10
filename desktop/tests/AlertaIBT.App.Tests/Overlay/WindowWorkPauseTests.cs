using AlertaIBT.App.Hosting;
using AlertaIBT.App.Overlay;
using AlertaIBT.App.Platform;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;
using Microsoft.Win32;
using Serilog;
using Serilog.Events;

namespace AlertaIBT.App.Tests.Overlay;

// Batch 42: windows are not touched when nothing changed, and not at all while nobody can see the screen (locked session, display off, PC asleep); the session state
// reaches every error line of the log.
public sealed class WindowWorkPauseTests
{
    // The size of the primary screen, but far off every monitor, so the real bar window of these tests is never seen (batch 39).
    private static ScreenInfo OffScreen()
    {
        var real = MonitorService.Enumerate().First(s => s.IsPrimary);
        return real with { X = (int)TestWindows.OffScreenLeft - real.Width, Y = (int)TestWindows.OffScreenTop, IsPrimary = false };
    }

    private static BarSettings Invisible() => new() { Background = "#00000000", Foreground = "#00000000" };

    [Fact]
    public void Preparing_the_bar_again_for_the_same_place_does_not_call_SetWindowPos_again()
    {
        WpfTestHost.Run(() =>
        {
            using var window = new OverlayWindow { State = new SessionState() };
            var screen = OffScreen();

            window.Prepare("Aviso de teste", Invisible(), screen);
            var after = window.Placements;
            Assert.True(after >= 1, "the first Prepare has to place the window");

            window.Prepare("Outro aviso", Invisible(), screen); // the same screen, the same bar: the window is already there
            window.Prepare("Mais um", Invisible(), screen);

            Assert.Equal(after, window.Placements);
            window.Hide();
        });
    }

    [Fact]
    public void A_bar_moved_to_another_screen_size_is_placed_again()
    {
        WpfTestHost.Run(() =>
        {
            using var window = new OverlayWindow { State = new SessionState() };
            var screen = OffScreen();
            window.Prepare("Aviso", Invisible(), screen);
            var after = window.Placements;

            window.Prepare("Aviso", Invisible(), screen with { X = screen.X - 40 });

            Assert.True(window.Placements > after);
            window.Hide();
        });
    }

    [Fact]
    public void The_topmost_timer_does_nothing_while_the_session_is_locked_or_the_display_is_off()
    {
        WpfTestHost.Run(() =>
        {
            var state = new SessionState();
            using var window = new OverlayWindow { State = state };
            window.Prepare("Aviso", Invisible(), OffScreen());
            var placed = window.Placements;

            state.SetLocked(true);
            window.ReassertTopmost();
            state.SetLocked(false);
            state.SetDisplay(DisplayPower.Off);
            window.ReassertTopmost();

            Assert.Equal(placed, window.Placements);
            window.Hide();
        });
    }

    [Fact]
    public void Reapply_places_the_visible_bar_once_more_and_does_nothing_for_a_hidden_one()
    {
        WpfTestHost.Run(() =>
        {
            using var window = new OverlayWindow { State = new SessionState() };
            window.Reapply(); // hidden, never prepared: nothing to do
            Assert.Equal(0, window.Placements);

            window.Prepare("Aviso", Invisible(), OffScreen());
            var placed = window.Placements;
            window.Reapply();

            Assert.Equal(placed + 1, window.Placements);
            window.Hide();
            window.Reapply(); // hidden again
            Assert.Equal(placed + 1, window.Placements);
        });
    }

    // ---- the tracker: what Windows says becomes the state ----

    [Fact]
    public void The_session_switch_and_the_power_events_become_the_state()
    {
        var state = new SessionState();
        var marker = new SessionMarker(Path.Combine(Path.GetTempPath(), "alertaibt-tracker-" + Guid.NewGuid().ToString("N"), "session.txt"));
        marker.Begin("test");
        using var tracker = new SessionStateTracker(state, marker);

        tracker.Apply(SessionSwitchReason.SessionLock);
        Assert.True(state.Locked);
        Assert.True(state.ShouldPauseWindowWork);
        tracker.Apply(SessionSwitchReason.SessionUnlock);
        Assert.False(state.Locked);

        tracker.Apply(SessionSwitchReason.RemoteDisconnect);
        Assert.True(state.ShouldPauseWindowWork);
        tracker.Apply(SessionSwitchReason.RemoteConnect);
        Assert.False(state.ShouldPauseWindowWork);

        tracker.Apply(PowerModes.Suspend);
        Assert.True(state.ShouldPauseWindowWork);
        tracker.Apply(PowerModes.Resume);
        Assert.False(state.ShouldPauseWindowWork);
        Assert.Contains("ultimo retorno da suspensao", state.Describe(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, DisplayPower.Off)]
    [InlineData(1, DisplayPower.On)]
    [InlineData(2, DisplayPower.Dimmed)]
    [InlineData(7, DisplayPower.Unknown)]
    public void The_display_power_values_of_Windows_are_read_as_off_on_and_dimmed(int value, DisplayPower expected) =>
        Assert.Equal(expected, MessageWindow.DisplayPowerOf(value));

    // ---- the log: every error line carries the session ----

    [Fact]
    public void Every_error_line_carries_the_session_state_and_a_warning_does_not()
    {
        var state = new SessionState();
        state.SetLocked(true);
        state.SetDisplay(DisplayPower.Off);
        var folder = Path.Combine(Path.GetTempPath(), "alertaibt-enrich-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "log-.txt");
            using (var logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .Enrich.With(new SessionStateEnricher(state))
                .WriteTo.File(file, rollingInterval: RollingInterval.Day, outputTemplate: "[{Level:u3}] {Message:lj}{Sessao:l}{NewLine}{Exception}")
                .CreateLogger())
            {
                logger.Warning("um aviso");
                logger.Error("um erro");
                logger.Fatal("um erro fatal");
                logger.Information("uma informacao");
            }

            var lines = File.ReadAllLines(Directory.GetFiles(folder).Single());
            Assert.Equal("[WRN] um aviso", lines[0]);
            Assert.StartsWith("[ERR] um erro [sessao: bloqueada, tela desligada", lines[1], StringComparison.Ordinal);
            Assert.StartsWith("[FTL] um erro fatal [sessao: bloqueada", lines[2], StringComparison.Ordinal);
            Assert.Equal("[INF] uma informacao", lines[3]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // Guard of the guard: the enricher never needs the log level to be passed along (Warning is below Error).
    [Fact]
    public void The_enricher_ignores_levels_below_error()
    {
        var enricher = new SessionStateEnricher(new SessionState());
        var template = new Serilog.Parsing.MessageTemplateParser().Parse("x");
        var info = new LogEvent(DateTimeOffset.Now, LogEventLevel.Warning, null, template, []);

        enricher.Enrich(info, new TestFactory());

        Assert.DoesNotContain(SessionStateEnricher.PropertyName, info.Properties.Keys);
    }

    private sealed class TestFactory : Serilog.Core.ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) =>
            new(name, new ScalarValue(value));
    }
}

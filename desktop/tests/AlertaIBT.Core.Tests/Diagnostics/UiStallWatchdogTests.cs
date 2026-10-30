using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Tests.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Diagnostics;

// The watchdog that notices a stopped screen, the breadcrumbs it prints and the settings diff that feeds them, all on a fake clock.
public sealed class UiStallWatchdogTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 30, 14, 36, 0, TimeSpan.Zero));
    private readonly CapturingLogger _log = new();
    private readonly Queue<Action> _ui = new();

    private UiStallWatchdog Make(BreadcrumbTrail? trail = null, Func<string>? context = null)
    {
        var watchdog = new UiStallWatchdog(action => _ui.Enqueue(action), _log, _clock, trail ?? new BreadcrumbTrail(clock: _clock), context);
        watchdog.Start();
        return watchdog;
    }

    // The "UI thread" answers what it was asked.
    private void UiRuns()
    {
        while (_ui.Count > 0)
        {
            _ui.Dequeue()();
        }
    }

    private void Advance(TimeSpan span)
    {
        var left = span;
        while (left > TimeSpan.Zero)
        {
            var step = left < UiStallWatchdog.PingEvery ? left : UiStallWatchdog.PingEvery;
            _clock.Advance(step);
            left -= step;
        }
    }

    private IEnumerable<string> Warnings => _log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);

    [Fact]
    public void A_screen_that_answers_never_logs_anything()
    {
        using var watchdog = Make();

        for (var i = 0; i < 40; i++)
        {
            Advance(UiStallWatchdog.PingEvery);
            UiRuns();
        }

        Assert.Empty(_log.Entries);
        Assert.Equal(0, watchdog.Stalls);
    }

    [Fact]
    public void A_screen_that_is_slow_but_answers_within_two_seconds_is_not_a_stall()
    {
        using var watchdog = Make();

        Advance(UiStallWatchdog.PingEvery);
        Advance(TimeSpan.FromSeconds(1.5));
        UiRuns();
        Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(Warnings);
    }

    [Fact]
    public void More_than_two_seconds_without_an_answer_logs_one_warning_with_the_breadcrumbs_and_then_the_recovery()
    {
        var trail = new BreadcrumbTrail(clock: _clock);
        trail.Add("configuracao: Player.WriteLoudnessTags");
        trail.Add("volume: medindo 07 Holy Forever.flac");
        using var watchdog = Make(trail, () => "GDI 300, USER 120");

        Advance(UiStallWatchdog.PingEvery); // the ping goes out and nobody answers
        Advance(TimeSpan.FromSeconds(1.9));
        Assert.Empty(Warnings);
        Advance(TimeSpan.FromSeconds(0.5));

        var warning = Assert.Single(Warnings);
        Assert.StartsWith("Interface parada ha 2.", warning, StringComparison.Ordinal);
        Assert.Contains("GDI 300, USER 120", warning, StringComparison.Ordinal);
        Assert.Contains("Player.WriteLoudnessTags", warning, StringComparison.Ordinal);
        Assert.Contains("volume: medindo 07 Holy Forever.flac", warning, StringComparison.Ordinal);
        Assert.True(watchdog.IsStalled);

        Advance(TimeSpan.FromSeconds(3));
        UiRuns(); // it comes back
        Assert.False(watchdog.IsStalled);
        var recovered = Warnings.Last();
        Assert.StartsWith("Interface voltou a responder; ficou parada por ", recovered, StringComparison.Ordinal);
        Assert.Equal(2, Warnings.Count());
        Assert.InRange(watchdog.LongestStall.TotalSeconds, 5.0, 6.0);
        Assert.Equal(1, watchdog.Stalls);
    }

    [Fact]
    public void A_long_stall_repeats_the_warning_every_ten_seconds_not_every_tick()
    {
        using var watchdog = Make();

        Advance(UiStallWatchdog.PingEvery);
        Advance(TimeSpan.FromSeconds(25));

        var warnings = Warnings.ToList();
        Assert.Equal(3, warnings.Count); // at 2 s, 12 s and 22 s
        Assert.StartsWith("Interface parada", warnings[0], StringComparison.Ordinal);
        Assert.StartsWith("Interface continua parada", warnings[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_dispatcher_that_is_closing_stops_the_watch_quietly()
    {
        var watchdog = new UiStallWatchdog(_ => throw new InvalidOperationException("closing"), _log, _clock, new BreadcrumbTrail(clock: _clock));
        watchdog.Start();

        Advance(TimeSpan.FromSeconds(10));

        Assert.Empty(Warnings);
        watchdog.Dispose();
    }

    [Fact]
    public void Disposing_stops_the_timer()
    {
        var watchdog = Make();
        watchdog.Dispose();

        Advance(TimeSpan.FromSeconds(10));

        Assert.Empty(_ui);
    }

    [Fact]
    public void The_breadcrumbs_keep_only_the_newest_entries_oldest_first_and_say_how_long_ago()
    {
        var trail = new BreadcrumbTrail(3, _clock);
        foreach (var name in new[] { "a", "b", "c", "d" })
        {
            trail.Add(name);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        var text = trail.Format(_clock.GetUtcNow());

        Assert.DoesNotContain(" a |", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf(" b", StringComparison.Ordinal) < text.IndexOf(" c", StringComparison.Ordinal));
        Assert.True(text.IndexOf(" c", StringComparison.Ordinal) < text.IndexOf(" d", StringComparison.Ordinal));
        Assert.Contains("(-3.0 s) b", text, StringComparison.Ordinal);
        Assert.Contains("(-1.0 s) d", text, StringComparison.Ordinal);
        Assert.Equal(3, trail.Snapshot().Count);
    }

    [Fact]
    public void An_empty_trail_says_so()
    {
        Assert.Contains("nenhuma", new BreadcrumbTrail(clock: _clock).Format(_clock.GetUtcNow()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_breadcrumbs_can_be_written_from_many_threads()
    {
        var trail = new BreadcrumbTrail(clock: _clock);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(
            () =>
            {
                for (var i = 0; i < 1000; i++)
                {
                    trail.Add($"t{t}-{i}");
                }
            },
            TestContext.Current.CancellationToken)));

        Assert.Equal(BreadcrumbTrail.DefaultCapacity, trail.Snapshot().Count);
    }

    // ---- what the settings breadcrumb names ----

    [Fact]
    public void The_settings_diff_names_the_keys_that_changed_and_never_their_values()
    {
        var before = new AppSettings();
        var after = before with
        {
            Player = before.Player with { WriteLoudnessTags = !before.Player.WriteLoudnessTags },
            Telegram = before.Telegram with { TokenProtected = "P:SECRET-VALUE" },
        };

        var text = SettingsDiff.Keys(before, after);

        Assert.Contains("Player.WriteLoudnessTags", text, StringComparison.Ordinal);
        Assert.Contains("Telegram.TokenProtected", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", text, StringComparison.Ordinal);
        Assert.Equal("(nada)", SettingsDiff.Keys(before, before with { }));
    }

    [Fact]
    public void A_settings_handler_that_keeps_the_caller_too_long_is_reported_by_name_and_time()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-slow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var told = new List<string>();
            using var settings = new SettingsService(new SettingsStore(Path.Combine(dir, "settings.json")), onSlowHandler: told.Add);
            settings.Changed += SlowHandler;

            settings.Update(s => s with { Player = s.Player with { WriteLoudnessTags = true } });

            var text = Assert.Single(told);
            Assert.Contains("SlowHandler", text, StringComparison.Ordinal);
            Assert.Contains("ms", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void SlowHandler(AppSettings settings) => Thread.Sleep(120);
}

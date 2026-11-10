using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Tests.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Diagnostics;

// One line per outage of a service a loop keeps asking, not one per try (a closed Holyrics filled 95 % of the daily log).
public sealed class OutageLoggerTests
{
    private readonly FakeTimeProvider _clock = new();
    private readonly CapturingLogger _log = new();

    private OutageLogger Make(OutageWording? wording = null, TimeSpan? reminderEvery = null, TimeSpan? gap = null) =>
        new(_log, wording ?? new OutageWording("Holyrics"), _clock, reminderEvery, gap);

    private IReadOnlyList<(LogLevel Level, string Message)> Told => _log.Entries.Where(e => e.Level >= LogLevel.Information).ToList();

    [Fact]
    public void The_first_failure_is_logged_in_full_as_a_warning()
    {
        var outage = Make();

        outage.Failed("refused", "Holyrics: cronometro falhou: conexao recusada em 192.168.1.13:8091");

        var line = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal("Holyrics: cronometro falhou: conexao recusada em 192.168.1.13:8091", line.Message);
        Assert.True(outage.IsDown);
        Assert.Equal(1, outage.Attempts);
    }

    [Fact]
    public void The_level_of_the_first_line_can_be_chosen()
    {
        Make().Failed("lost", "Banco de dados: nova tentativa em 1 s", level: LogLevel.Information);

        Assert.Equal(LogLevel.Information, Assert.Single(_log.Entries).Level);
    }

    [Fact]
    public void The_repeats_are_silent_at_information_and_above_and_only_debug_at_most()
    {
        var outage = Make();

        for (var i = 0; i < 100; i++)
        {
            outage.Failed("refused", "Holyrics: falhou");
            _clock.Advance(TimeSpan.FromSeconds(17));
        }

        Assert.Single(Told); // 100 tries over 28 minutes: the warning, nothing else
        Assert.All(_log.Entries.Skip(1), e => Assert.Equal(LogLevel.Debug, e.Level));
        Assert.Equal(100, outage.Attempts);
    }

    [Fact]
    public void A_success_after_a_failure_says_how_long_and_how_many_tries_in_one_information_line()
    {
        var outage = Make();
        for (var i = 0; i < 31; i++)
        {
            outage.Failed("refused", "Holyrics: falhou");
            _clock.Advance(TimeSpan.FromSeconds(10));
        }

        outage.Succeeded();

        var recovered = Told.Last();
        Assert.Equal(LogLevel.Information, recovered.Level);
        Assert.Equal("Holyrics voltou a responder ap\U000000F3s 5 min, 31 tentativas", recovered.Message);
        Assert.False(outage.IsDown);
        Assert.Equal(0, outage.Attempts);
    }

    [Fact]
    public void A_single_try_is_told_in_the_singular_and_a_short_outage_in_seconds()
    {
        var outage = Make();
        outage.Failed("refused", "Holyrics: falhou");
        _clock.Advance(TimeSpan.FromSeconds(40));

        outage.Succeeded();

        Assert.Equal("Holyrics voltou a responder ap\U000000F3s 40 s, 1 tentativa", Told.Last().Message);
    }

    [Fact]
    public void A_success_with_no_outage_says_nothing_and_a_second_success_neither()
    {
        var outage = Make();
        outage.Succeeded();
        outage.Failed("refused", "Holyrics: falhou");
        outage.Succeeded();
        outage.Succeeded();

        Assert.Equal(2, Told.Count);
    }

    [Fact]
    public void A_failure_after_a_recovery_is_a_new_episode_with_its_own_warning_and_count()
    {
        var outage = Make();
        outage.Failed("refused", "primeira");
        outage.Failed("refused", "primeira");
        _clock.Advance(TimeSpan.FromMinutes(2));
        outage.Succeeded();
        _clock.Advance(TimeSpan.FromMinutes(30));

        outage.Failed("refused", "segunda");
        _clock.Advance(TimeSpan.FromMinutes(3));
        outage.Succeeded();

        Assert.Equal(
            [LogLevel.Warning, LogLevel.Information, LogLevel.Warning, LogLevel.Information],
            Told.Select(l => l.Level));
        Assert.Equal("primeira", Told[0].Message);
        Assert.Equal("segunda", Told[2].Message);
        Assert.Equal("Holyrics voltou a responder ap\U000000F3s 3 min, 1 tentativa", Told[3].Message);
    }

    [Fact]
    public void While_it_stays_down_there_is_one_reminder_per_hour_counted_from_the_start()
    {
        var outage = Make();

        for (var minutes = 0; minutes <= 190; minutes += 10)
        {
            outage.Failed("refused", "Holyrics: falhou", "conexao recusada");
            _clock.Advance(TimeSpan.FromMinutes(10));
        }

        var lines = Told;
        Assert.Equal(4, lines.Count); // the warning, then the 1 h, 2 h and 3 h marks
        Assert.Equal("Holyrics continua sem resposta h\U000000E1 1 h (conexao recusada)", lines[1].Message);
        Assert.Equal("Holyrics continua sem resposta h\U000000E1 2 h (conexao recusada)", lines[2].Message);
        Assert.Equal("Holyrics continua sem resposta h\U000000E1 3 h (conexao recusada)", lines[3].Message);
        Assert.All(lines.Skip(1), l => Assert.Equal(LogLevel.Information, l.Level));
    }

    [Fact]
    public void A_reminder_skipped_by_a_long_pause_is_told_once_with_the_hours_that_passed()
    {
        var outage = Make();
        outage.Failed("refused", "Holyrics: falhou");

        _clock.Advance(TimeSpan.FromMinutes(150));
        outage.Failed("refused", "Holyrics: falhou");
        outage.Failed("refused", "Holyrics: falhou");

        Assert.Equal(2, Told.Count);
        Assert.Equal("Holyrics continua sem resposta h\U000000E1 2 h", Told[1].Message);
    }

    [Fact]
    public void A_different_reason_is_told_when_it_comes_but_never_more_than_once_per_gap()
    {
        var outage = Make(gap: TimeSpan.FromMinutes(5));
        outage.Failed("refused", "recusada");
        _clock.Advance(TimeSpan.FromMinutes(1));
        outage.Failed("denied", "sem permissao"); // inside the gap: silent
        Assert.Single(Told);

        _clock.Advance(TimeSpan.FromMinutes(5));
        outage.Failed("denied", "sem permissao"); // past the gap: the reason changed, so it is told
        Assert.Equal(["recusada", "sem permissao"], Told.Select(l => l.Message));

        _clock.Advance(TimeSpan.FromMinutes(1));
        outage.Failed("denied", "sem permissao"); // the same reason again: silent
        Assert.Equal(2, Told.Count);
    }

    [Fact]
    public void Reset_ends_the_episode_without_a_word()
    {
        var outage = Make();
        outage.Failed("refused", "Holyrics: falhou");
        outage.Reset();
        _clock.Advance(TimeSpan.FromHours(3));

        outage.Succeeded();

        Assert.Single(Told);
        Assert.False(outage.IsDown);

        outage.Failed("refused", "Holyrics: falhou outra vez"); // a failure after a Reset is a new episode
        Assert.Equal(2, Told.Count);
    }

    [Fact]
    public void The_words_of_the_service_are_used_in_the_reminder_and_the_recovery()
    {
        var outage = Make(new OutageWording("Art-Net", "sem abrir a porta 6454", "abriu a porta 6454"));
        outage.Failed("AddressInUse", "Art-Net: nao abriu");
        _clock.Advance(TimeSpan.FromMinutes(61));
        outage.Failed("AddressInUse", "Art-Net: nao abriu");
        outage.Succeeded();

        Assert.Equal("Art-Net continua sem abrir a porta 6454 h\U000000E1 1 h", Told[1].Message);
        Assert.Equal("Art-Net abriu a porta 6454 ap\U000000F3s 1 h 1 min, 2 tentativas", Told[2].Message);
    }

    [Fact]
    public void The_reminder_period_can_be_changed()
    {
        var outage = Make(reminderEvery: TimeSpan.FromMinutes(10));
        outage.Failed("refused", "x");
        _clock.Advance(TimeSpan.FromMinutes(10));
        outage.Failed("refused", "x");

        Assert.Equal("Holyrics continua sem resposta h\U000000E1 10 min", Told[1].Message);
    }

    [Theory]
    [InlineData(0, "1 s")]
    [InlineData(40, "40 s")]
    [InlineData(59, "59 s")]
    [InlineData(60, "1 min")]
    [InlineData(300, "5 min")]
    [InlineData(3599, "59 min")]
    [InlineData(3600, "1 h")]
    [InlineData(3660, "1 h 1 min")]
    [InlineData(7200, "2 h")]
    [InlineData(93600, "26 h")]
    public void A_duration_is_told_in_seconds_minutes_or_hours(int seconds, string expected)
    {
        Assert.Equal(expected, OutageLogger.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Without_a_logger_nothing_happens_and_nothing_throws()
    {
        var outage = new OutageLogger(null, new OutageWording("X"), _clock);

        outage.Failed("a", "b");
        outage.Failed("a", "b");
        outage.Succeeded();

        Assert.False(outage.IsDown);
    }

    [Fact]
    public async Task Many_threads_failing_together_still_make_one_warning()
    {
        var outage = Make();

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(
            () =>
            {
                for (var i = 0; i < 200; i++)
                {
                    outage.Failed("refused", "Holyrics: falhou");
                }
            },
            TestContext.Current.CancellationToken)));

        Assert.Single(Told);
        Assert.Equal(3200, outage.Attempts);
    }
}

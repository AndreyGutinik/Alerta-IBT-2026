using AlertaIBT.Core.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Diagnostics;

// Batch 42: how the next start learns what ended the previous run (the marker with its heartbeat and its power events, and the Windows event log), on a fake clock.
// The three "abnormal ends" of the owner's log (2026-11-05 14:04, 11-05 23:12, 11-07 22:59) were normal Windows shutdowns that killed the process before OnExit ran.
public sealed class RunEndTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 16, 50, 19, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alertaibt-runend-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(Start);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string File => Path.Combine(_dir, "session.txt");

    private SessionMarker Marker() => new(File, _clock);

    private static WindowsEvent Event(DateTimeOffset at, string provider, int id, string detail = "", string log = "System") => new(at, log, provider, id, detail);

    // ---- the marker ----

    [Fact]
    public void The_heartbeat_moves_the_last_sign_of_life_and_the_file_is_written_atomically()
    {
        var marker = Marker();
        marker.Begin("8.0.6");
        for (var i = 0; i < 5; i++)
        {
            _clock.Advance(SessionMarker.HeartbeatEvery);
            marker.Beat();
        }

        var previous = Marker().Begin("8.0.7");

        Assert.Equal(Start + (5 * SessionMarker.HeartbeatEvery), previous.LastHeartbeat);
        Assert.False(System.IO.File.Exists(File + ".tmp"));
        Assert.True(previous.EndedAbnormally);
        Assert.Equal("running", previous.State);
    }

    [Fact]
    public void The_heartbeat_timer_writes_the_marker_by_itself_and_stops_at_the_end()
    {
        var marker = Marker();
        marker.Begin("8.0.6");
        marker.StartHeartbeat();

        _clock.Advance(SessionMarker.HeartbeatEvery);
        var beating = Marker().Begin("8.0.7");
        Assert.Equal(Start + SessionMarker.HeartbeatEvery, beating.LastHeartbeat);

        marker.End("saida normal");
        _clock.Advance(TimeSpan.FromMinutes(10)); // nothing more is written after the end
        var after = Marker().Begin("8.0.8");

        Assert.True(after.EndedCleanly);
        Assert.Equal("saida normal", after.EndReason);
    }

    [Fact]
    public void Windows_asking_the_app_to_end_counts_as_a_clean_end_even_when_the_process_is_killed_before_OnExit()
    {
        var marker = Marker();
        marker.Begin("8.0.6-pre.1");
        _clock.Advance(TimeSpan.FromMinutes(14));
        marker.SessionEnding("Windows encerrando a sessao (Shutdown)"); // then Windows kills the process: End is never called

        var previous = Marker().Begin("8.0.6-pre.1");

        Assert.True(previous.EndedCleanly);
        Assert.False(previous.EndedAbnormally);
        Assert.Equal("session-ending", previous.State);
        Assert.Equal("Windows encerrando a sessao (Shutdown)", previous.EndReason);
        Assert.NotNull(previous.SessionEndingAt);
    }

    [Fact]
    public void A_shutdown_that_was_cancelled_does_not_hide_a_later_crash()
    {
        var marker = Marker();
        marker.Begin("8.0.6");
        marker.SessionEnding("Windows encerrando a sessao (Logoff)");
        _clock.Advance(TimeSpan.FromSeconds(40)); // the process lived on: the logoff was refused by another program
        marker.Beat();

        var previous = Marker().Begin("8.0.6");

        Assert.Equal("running", previous.State);
        Assert.True(previous.EndedAbnormally);
    }

    [Fact]
    public void A_crash_records_the_type_of_the_exception_and_never_its_message()
    {
        var marker = Marker();
        marker.Begin("8.0.6");
        _clock.Advance(TimeSpan.FromMinutes(3));
        marker.Crash("NullReferenceException");
        marker.End("saida normal"); // an end after a crash does not erase it

        var previous = Marker().Begin("8.0.6");

        Assert.Equal("NullReferenceException", previous.CrashType);
        Assert.True(previous.EndedAbnormally);
        Assert.DoesNotContain("saida normal", System.IO.File.ReadAllText(File), StringComparison.Ordinal);
    }

    [Fact]
    public void The_power_events_are_kept_and_a_resume_clears_the_suspended_state()
    {
        var marker = Marker();
        marker.Begin("8.0.6");
        marker.Suspending();
        Assert.Equal("suspending", Marker().Begin("x").State);

        var again = Marker();
        again.Begin("8.0.6");
        again.Suspending();
        _clock.Advance(TimeSpan.FromHours(2));
        again.Resumed();

        var previous = Marker().Begin("8.0.6");
        Assert.Equal("running", previous.State);
        Assert.NotNull(previous.SuspendedAt);
        Assert.NotNull(previous.ResumedAt);
    }

    // ---- the sentence ----

    [Fact]
    public void A_clean_end_says_nothing()
    {
        var marker = Marker();
        marker.Begin("8.0.6");
        marker.End("saida normal");

        var report = RunEndDiagnosis.Describe(Marker().Begin("8.0.6"), [], true);

        Assert.Equal(RunEndKind.Clean, report.Kind);
        Assert.Equal(string.Empty, report.Message);
    }

    [Fact]
    public void A_windows_shutdown_that_the_app_heard_of_is_information_and_names_the_event_1074()
    {
        var marker = Marker();
        marker.Begin("8.0.6-pre.1");
        _clock.Advance(TimeSpan.FromMinutes(14));
        marker.SessionEnding("Windows encerrando a sessao (Shutdown)");
        var shutdownAt = _clock.GetUtcNow();

        var report = RunEndDiagnosis.Describe(Marker().Begin("8.0.6"), [Event(shutdownAt, "User32", 1074, "restart, pedido por StartMenuExperienceHost.exe")], true);

        Assert.Equal(RunEndKind.WindowsEndedSession, report.Kind);
        Assert.False(report.IsWarning);
        Assert.Contains("evento 1074", report.Message, StringComparison.Ordinal);
        Assert.Contains("StartMenuExperienceHost.exe", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_kernel_power_41_event_says_power_loss_or_a_forced_shutdown_with_the_last_sign_of_life()
    {
        var marker = Marker();
        marker.Begin("8.0.6");
        _clock.Advance(TimeSpan.FromMinutes(5));
        marker.Beat();
        var lastSign = _clock.GetUtcNow();
        var cut = lastSign + TimeSpan.FromSeconds(20);

        var report = RunEndDiagnosis.Describe(Marker().Begin("8.0.6"), [Event(cut, "Microsoft-Windows-Kernel-Power", 41)], true);

        Assert.Equal(RunEndKind.PowerLoss, report.Kind);
        Assert.True(report.IsWarning);
        Assert.Contains("queda de energia", report.Message, StringComparison.Ordinal);
        Assert.Contains("evento 41", report.Message, StringComparison.Ordinal);
        Assert.Contains(lastSign.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_event_6008_also_means_an_unexpected_shutdown()
    {
        var marker = Marker();
        marker.Begin("8.0.6");

        var report = RunEndDiagnosis.Describe(Marker().Begin("8.0.6"), [Event(Start + TimeSpan.FromMinutes(9), "EventLog", 6008)], true);

        Assert.Equal(RunEndKind.PowerLoss, report.Kind);
        Assert.Contains("evento 6008", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_planned_shutdown_the_app_never_heard_of_is_named_with_the_event_1074_and_is_not_a_warning()
    {
        var marker = Marker();
        marker.Begin("8.0.6");
        _clock.Advance(TimeSpan.FromMinutes(2));
        marker.Beat();

        var report = RunEndDiagnosis.Describe(Marker().Begin("8.0.6"), [Event(_clock.GetUtcNow() + TimeSpan.FromSeconds(5), "User32", 1074, "power off")], true);

        Assert.Equal(RunEndKind.WindowsShutdown, report.Kind);
        Assert.False(report.IsWarning);
        Assert.Contains("desligou ou reiniciou", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_PC_that_suspended_and_never_resumed_says_so_from_the_marker()
    {
        var marker = Marker();
        marker.Begin("8.0.6");
        _clock.Advance(TimeSpan.FromMinutes(7));
        marker.Suspending();

        var report = RunEndDiagnosis.Describe(Marker().Begin("8.0.6"), [], true);

        Assert.Equal(RunEndKind.SleptAndNeverWoke, report.Kind);
        Assert.Contains("suspenso", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_PC_that_slept_in_the_windows_log_without_a_resume_after_it_says_so_but_a_resume_cancels_it()
    {
        var marker = Marker();
        marker.Begin("8.0.6");
        _clock.Advance(TimeSpan.FromMinutes(7));
        marker.Beat();
        var sleep = _clock.GetUtcNow();

        var asleep = RunEndDiagnosis.Describe(Marker().Begin("8.0.6"), [Event(sleep, "Microsoft-Windows-Kernel-Power", 42)], true);
        var awake = RunEndDiagnosis.Describe(Marker().Begin("8.0.6"), [Event(sleep, "Microsoft-Windows-Kernel-Power", 42), Event(sleep + TimeSpan.FromHours(1), "Microsoft-Windows-Kernel-Power", 107)], true);

        Assert.Equal(RunEndKind.SleptAndNeverWoke, asleep.Kind);
        Assert.Equal(RunEndKind.AppGone, awake.Kind);
    }

    [Fact]
    public void A_crash_recorded_by_Windows_for_this_program_is_a_native_crash_and_one_for_another_program_never_reaches_the_diagnosis()
    {
        var marker = Marker();
        marker.Begin("8.0.6");

        var report = RunEndDiagnosis.Describe(
            Marker().Begin("8.0.6"),
            [Event(Start + TimeSpan.FromMinutes(1), "Application Error", 1000, "modulo coreclr.dll", "Application")],
            true);

        Assert.Equal(RunEndKind.NativeCrash, report.Kind);
        Assert.Contains("evento 1000", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_in_the_windows_log_says_the_app_ended_with_the_windows_on_and_what_may_have_done_it()
    {
        var marker = Marker();
        marker.Begin("8.0.6");
        _clock.Advance(TimeSpan.FromMinutes(1));
        marker.Beat();

        var report = RunEndDiagnosis.Describe(Marker().Begin("8.0.6"), [], true);

        Assert.Equal(RunEndKind.AppGone, report.Kind);
        Assert.True(report.IsWarning);
        Assert.Contains("Windows ligado", report.Message, StringComparison.Ordinal);
        Assert.Contains("Gerenciador de Tarefas", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void When_the_windows_log_could_not_be_read_the_sentence_says_so_instead_of_guessing()
    {
        var marker = Marker();
        marker.Begin("8.0.6");

        var report = RunEndDiagnosis.Describe(Marker().Begin("8.0.6"), [], false);

        Assert.Equal(RunEndKind.AppGone, report.Kind);
        Assert.Contains("log do Windows n\U000000E3o p\U000000F4de ser lido", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_event_from_before_the_run_was_alive_does_not_count()
    {
        var marker = Marker();
        marker.Begin("8.0.6");
        _clock.Advance(TimeSpan.FromMinutes(30));
        marker.Beat();

        // A power loss two days ago is not the reason this run ended.
        var report = RunEndDiagnosis.Describe(Marker().Begin("8.0.6"), [Event(Start - TimeSpan.FromDays(2), "Microsoft-Windows-Kernel-Power", 41)], true);

        Assert.Equal(RunEndKind.AppGone, report.Kind);
    }

    [Fact]
    public void A_quit_that_froze_and_was_ended_by_force_is_reported_with_its_step()
    {
        var marker = Marker();
        marker.Begin("8.0.6");
        marker.End("encerramento travado em player");

        var report = RunEndDiagnosis.Describe(Marker().Begin("8.0.6"), [], true);

        Assert.Equal(RunEndKind.StuckExit, report.Kind);
        Assert.True(report.IsWarning);
        Assert.Contains("player", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unhandled_exception_is_reported_with_its_type_only()
    {
        var marker = Marker();
        marker.Begin("8.0.6");
        marker.Crash("InvalidOperationException");

        var report = RunEndDiagnosis.Describe(Marker().Begin("8.0.6"), [], true);

        Assert.Equal(RunEndKind.Crash, report.Kind);
        Assert.Contains("InvalidOperationException", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_text_has_the_accented_words_of_the_language()
    {
        var marker = Marker();
        marker.Begin("8.0.6");

        var report = RunEndDiagnosis.Describe(Marker().Begin("8.0.6"), [Event(Start + TimeSpan.FromMinutes(1), "Microsoft-Windows-Kernel-Power", 41)], true);

        Assert.Contains("execu\U000000E7\U000000E3o", report.Message, StringComparison.Ordinal);
        Assert.Contains("for\U000000E7ado", report.Message, StringComparison.Ordinal);
    }
}

using System.Net.Http;
using AlertaIBT.App.Platform;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Velopack.Sources;

namespace AlertaIBT.App.Tests.Platform;

// The automatic update: opens -> looks (5 s later) -> downloads -> applies and restarts when the app is idle; never throws.
public sealed class UpdateServiceTests : IDisposable
{
    private const string Baixando = "Baixando a atualiza\U000000E7\U000000E3o 8.1.0\U00002026";
    private const string Instalando = "Instalando e reiniciando\U00002026";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-update-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    public UpdateServiceTests()
    {
        Directory.CreateDirectory(_dir);
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 14, 30, 0, TimeSpan.Zero));
        Settings = new SettingsService(new SettingsStore(Path.Combine(_dir, "settings.json"), Time), Time);
        Client = new FakeClient(Time, Calls);
        Host = new FakeHost(Time, Calls);
    }

    private FakeTimeProvider Time { get; }

    private SettingsService Settings { get; }

    private FakeClient Client { get; }

    private FakeHost Host { get; }

    private List<string> Calls { get; } = [];

    private ListLogger Logger { get; } = new();

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        Settings.Dispose();
        if (Directory.Exists(_dir))
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
                // A scanner may hold a file for a moment; the temp folder is cleaned later.
            }
        }
    }

    private sealed class ListLogger : ILogger<UpdateService>
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
            {
                Lines.Add((logLevel, formatter(state, exception)));
            }
        }

        public int Count(Func<(LogLevel Level, string Text), bool> match)
        {
            lock (Lines)
            {
                return Lines.Count(match);
            }
        }
    }

    private sealed class FakeClient(FakeTimeProvider time, List<string> calls) : IUpdateClient
    {
        public bool IsInstalled { get; set; } = true;

        public string CurrentVersion { get; set; } = "8.0.0";

        public UpdateOffer? Pending { get; set; }

        public UpdateOffer? Next { get; set; }

        public Exception? CheckFails { get; set; }

        public Exception? DownloadFails { get; set; }

        public Exception? ApplyFails { get; set; }

        public bool CheckHangsUntilCancelled { get; set; }

        public int[] ProgressSteps { get; set; } = [];

        public int Checks;
        public List<DateTimeOffset> CheckTimes { get; } = [];
        public List<UpdateOffer> Downloads { get; } = [];
        public List<(UpdateOffer Offer, string[] Args)> Applies { get; } = [];
        public TaskCompletionSource FirstCheck { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<UpdateOffer?> CheckAsync(CancellationToken cancellationToken)
        {
            lock (CheckTimes)
            {
                CheckTimes.Add(time.GetUtcNow());
            }

            Interlocked.Increment(ref Checks);
            FirstCheck.TrySetResult();
            if (CheckHangsUntilCancelled)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return CheckFails is not null ? throw CheckFails : Next;
        }

        public Task DownloadAsync(UpdateOffer offer, IProgress<int>? progress, CancellationToken cancellationToken)
        {
            if (DownloadFails is not null)
            {
                throw DownloadFails;
            }

            foreach (var step in ProgressSteps)
            {
                progress?.Report(step);
            }

            lock (calls)
            {
                calls.Add("download");
            }

            Downloads.Add(offer);
            return Task.CompletedTask;
        }

        public void ApplyAndRestart(UpdateOffer offer, string[] restartArguments)
        {
            if (ApplyFails is not null)
            {
                throw ApplyFails;
            }

            lock (calls)
            {
                calls.Add("apply");
            }

            Applies.Add((offer, restartArguments));
        }
    }

    private sealed class FakeHost(FakeTimeProvider time, List<string> calls) : IUpdateHost
    {
        private Action? _signal;

        public volatile bool Idle = true;
        public int Exits;
        public int Prepares;
        public int IdleChecks;
        public List<DateTimeOffset> IdleCheckTimes { get; } = [];

        public bool IsIdle
        {
            get
            {
                Interlocked.Increment(ref IdleChecks);
                lock (IdleCheckTimes)
                {
                    IdleCheckTimes.Add(time.GetUtcNow());
                }

                return Idle;
            }
        }

        public bool Attached => _signal is not null;

        public void Attach(Action maybeIdle) => _signal = maybeIdle;

        public void Signal() => _signal?.Invoke();

        public void PrepareForRestart()
        {
            Interlocked.Increment(ref Prepares);
            lock (calls)
            {
                calls.Add("prepare");
            }
        }

        public void Exit()
        {
            Interlocked.Increment(ref Exits);
            lock (calls)
            {
                calls.Add("exit");
            }
        }
    }

    // How many times the updater failed to install (the service logs one line per failure, after it let go of the install).
    private int FailedInstalls() => Logger.Count(l => l.Level == LogLevel.Warning && l.Text.StartsWith("Falha ao instalar", StringComparison.Ordinal));

    private UpdateService Make(TimeSpan? checkTimeout = null)
    {
        var service = new UpdateService(Client, Host, Settings, Logger, Time, checkTimeout: checkTimeout);
        _disposables.Add(service);
        return service;
    }

    private static UpdateOffer Offer(string version) => new(version, version);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private void SetAutoUpdate(bool on) =>
        Settings.Update(s => s with { System = s.System with { AutoUpdate = on } });

    private async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "condition was not met in time");
            await Task.Delay(5, Ct);
        }
    }

    // Moves the fake clock in small steps, giving the background loops time to run in between, until the condition holds.
    private async Task Pump(Func<bool> condition, TimeSpan step, int maxSteps = 400)
    {
        for (var i = 0; i < maxSteps && !condition(); i++)
        {
            Time.Advance(step);
            await Task.Delay(3, Ct);
        }

        await WaitUntil(condition);
    }

    // --- the basics ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_copy_that_is_not_installed_never_asks_the_feed()
    {
        Client.IsInstalled = false;
        Client.Next = Offer("9.0.0");
        var service = Make();

        Assert.Equal(UpdateCheckResult.NotInstalled, await service.CheckOnceAsync(Ct));
        Assert.Equal(UpdateCheckResult.NotInstalled, await service.CheckOnceAsync(Ct, manual: true));
        service.Start(["--tray"]);
        Assert.False(service.TryApplyPendingAtStartup(["--tray"]));
        Time.Advance(TimeSpan.FromHours(3));
        await Task.Delay(30, Ct);

        Assert.Equal(0, Client.Checks);
        Assert.Empty(Client.Downloads);
        Assert.False(Host.Attached);
        Assert.Equal(UpdatePhase.Idle, service.State.Phase);
    }

    [Fact]
    public async Task No_newer_release_means_up_to_date_and_says_so()
    {
        Client.Next = null;
        var service = Make();
        service.Start([]);

        Assert.Equal(UpdateCheckResult.UpToDate, await service.CheckOnceAsync(Ct));

        Assert.Empty(Client.Downloads);
        Assert.Equal("Atualizado (8.0.0) \U000000E0s 14:30", service.State.Result);
        Assert.Null(service.State.Notice);
    }

    [Fact]
    public void The_defaults_are_the_ones_of_the_spec()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), UpdateService.DefaultFirstCheckDelay);
        Assert.Equal(TimeSpan.FromMinutes(30), UpdateService.DefaultInterval);
        Assert.Equal(TimeSpan.FromSeconds(15), UpdateService.DefaultIdlePoll);
        Assert.Equal(TimeSpan.FromSeconds(30), UpdateService.DefaultCheckTimeout);
    }

    // --- found, idle: download, then apply with the original arguments ------------------------------------------------

    [Fact]
    public async Task A_newer_release_found_while_idle_is_downloaded_then_applied_with_the_original_arguments()
    {
        Client.Next = Offer("8.1.0");
        var service = Make();
        service.Start(["--tray"]);

        Assert.Equal(UpdateCheckResult.Downloaded, await service.CheckOnceAsync(Ct));

        Assert.Equal(["download", "prepare", "apply", "exit"], Calls);
        var (offer, args) = Assert.Single(Client.Applies);
        Assert.Equal("8.1.0", offer.Version);
        Assert.Equal(["--tray"], args);
        Assert.Equal(UpdatePhase.Installing, service.State.Phase);
    }

    [Fact]
    public async Task The_attempt_is_on_disk_before_the_app_restarts()
    {
        Client.Next = Offer("8.1.0");
        var service = Make();
        service.Start([]);

        await service.CheckOnceAsync(Ct);

        var saved = new SettingsStore(Path.Combine(_dir, "settings.json"), Time).Load().Settings.System;
        Assert.Equal("8.1.0", saved.UpdateAttemptVersion);
        Assert.Equal(1, saved.UpdateAttempts);
        Assert.Equal(Time.GetUtcNow(), saved.UpdateAttemptSince);
        Assert.False(Settings.HasUnsavedChanges);
    }

    [Fact]
    public async Task The_notices_say_downloading_with_the_progress_and_then_installing()
    {
        Client.Next = Offer("8.1.0");
        Client.ProgressSteps = [0, 40, 100];
        var notices = new List<string?>();
        var service = Make();
        service.StateChanged += state => notices.Add(state.Notice);
        service.Start([]);

        await service.CheckOnceAsync(Ct);

        Assert.Contains(Baixando, notices);
        Assert.Contains(Baixando + " 40%", notices);
        Assert.Equal(Instalando, notices.Last(n => n is not null));
        Assert.Equal(Instalando, service.State.Notice);
    }

    [Fact]
    public async Task A_release_already_downloaded_is_not_downloaded_again_but_is_applied()
    {
        Client.Next = Offer("8.1.0");
        Client.Pending = Offer("8.1.0");
        var service = Make();
        service.Start([]);

        Assert.Equal(UpdateCheckResult.Downloaded, await service.CheckOnceAsync(Ct));
        await WaitUntil(() => Host.Exits == 1); // the package kept from before may already be on its way at the start

        Assert.Empty(Client.Downloads);
        Assert.Single(Client.Applies);
    }

    // --- busy: keep it pending and apply as soon as the app is idle -----------------------------------------------------

    [Fact]
    public async Task While_busy_the_update_is_downloaded_and_kept_pending()
    {
        Host.Idle = false;
        Client.Next = Offer("8.1.0");
        var service = Make();
        service.Start([]);

        Assert.Equal(UpdateCheckResult.Downloaded, await service.CheckOnceAsync(Ct));

        Assert.Equal(["download"], Calls);
        Assert.Empty(Client.Applies);
        Assert.Equal(0, Host.Exits);
        Assert.Equal(UpdatePhase.WaitingForIdle, service.State.Phase);
        Assert.Equal("Nova vers\U000000E3o 8.1.0 baixada, instalando quando ficar ocioso", service.State.Result);
        Assert.Null(service.State.Notice);
    }

    [Fact]
    public async Task When_the_app_becomes_idle_the_signal_applies_the_update_once()
    {
        Host.Idle = false;
        Client.Next = Offer("8.1.0");
        var service = Make();
        service.Start(["--tray"]);
        await service.CheckOnceAsync(Ct);

        Host.Idle = true;
        Host.Signal();
        await WaitUntil(() => Host.Exits == 1);

        Assert.Equal(["download", "prepare", "apply", "exit"], Calls);
        Assert.Equal(["--tray"], Assert.Single(Client.Applies).Args);
    }

    [Fact]
    public async Task Many_signals_at_once_apply_exactly_once()
    {
        Host.Idle = false;
        Client.Next = Offer("8.1.0");
        var service = Make();
        service.Start([]);
        await service.CheckOnceAsync(Ct);

        Host.Idle = true;
        Parallel.For(0, 64, _ => Host.Signal());
        await WaitUntil(() => Host.Exits >= 1);
        await Task.Delay(150, Ct); // a second apply would show up here

        Assert.Single(Client.Applies);
        Assert.Equal(1, Host.Exits);
        Assert.Equal(1, Host.Prepares);
    }

    [Fact]
    public async Task Without_any_signal_the_pending_update_is_applied_by_the_poll_every_15_seconds_never_faster()
    {
        Host.Idle = false;
        Client.Next = Offer("8.1.0");
        var service = Make();
        service.Start([]);
        await service.CheckOnceAsync(Ct);
        var before = Host.IdleChecks;

        await Pump(() => Host.IdleChecks >= before + 3, TimeSpan.FromSeconds(1));
        Host.Idle = true;
        await Pump(() => Host.Exits == 1, TimeSpan.FromSeconds(1));

        Assert.Single(Client.Applies);
        List<DateTimeOffset> times;
        lock (Host.IdleCheckTimes)
        {
            times = [.. Host.IdleCheckTimes.Skip(2)]; // the first two looks are the checks (mine and the loop's own, 5 s after the start)
        }

        for (var i = 1; i < times.Count; i++)
        {
            Assert.True(times[i] - times[i - 1] >= TimeSpan.FromSeconds(15), $"poll {i} came after {times[i] - times[i - 1]}; all: {string.Join(",", times.Select(t => t.ToString("mm:ss")))}");
        }
    }

    [Fact]
    public async Task A_package_downloaded_in_an_earlier_run_is_applied_at_the_next_idle_moment_without_any_network()
    {
        Client.Pending = Offer("8.1.0");
        var service = Make();

        service.Start(["--tray"]);
        await WaitUntil(() => Host.Exits == 1);

        Assert.Equal(0, Client.Checks);
        Assert.Equal("8.1.0", Assert.Single(Client.Applies).Offer.Version);
    }

    // --- offline, timeouts, cancellation ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("check")]
    [InlineData("download")]
    public async Task Offline_is_a_quiet_failure_not_an_exception_and_not_an_error_line(string where)
    {
        Client.Next = Offer("8.1.0");
        if (where == "check")
        {
            Client.CheckFails = new HttpRequestException("Nenhuma conexao");
        }
        else
        {
            Client.DownloadFails = new HttpRequestException("Nenhuma conexao");
        }

        var service = Make();
        service.Start([]);

        Assert.Equal(UpdateCheckResult.Failed, await service.CheckOnceAsync(Ct));

        Assert.Equal(0, Logger.Count(l => l.Level >= LogLevel.Warning));
        Assert.Contains(Logger.Lines, l => l.Text.Contains("Nenhuma conexao", StringComparison.Ordinal));
        Assert.Equal("Sem conex\U000000E3o com o GitHub", service.State.Result);
        Assert.Null(service.State.Notice);
        Assert.Empty(Client.Applies);
    }

    [Fact]
    public async Task A_feed_that_never_answers_ends_in_failure_after_the_timeout()
    {
        Client.CheckHangsUntilCancelled = true;
        var service = Make(checkTimeout: TimeSpan.FromMilliseconds(60));
        service.Start([]);

        var result = await service.CheckOnceAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(UpdateCheckResult.Failed, result);
        Assert.Equal("Sem conex\U000000E3o com o GitHub", service.State.Result);
    }

    [Fact]
    public async Task Cancelling_the_check_from_outside_is_honoured()
    {
        Client.CheckHangsUntilCancelled = true;
        var service = Make();
        service.Start([]);
        using var cts = new CancellationTokenSource();
        var running = service.CheckOnceAsync(cts.Token);

        await Client.FirstCheck.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task Start_returns_at_once_and_nothing_throws_even_if_the_client_blows_up()
    {
        var service = new UpdateService(new ThrowingClient(), Host, Settings, Logger, Time);
        _disposables.Add(service);

        service.Start([]);
        Assert.False(service.TryApplyPendingAtStartup([]));
        Assert.Equal(UpdateCheckResult.Failed, await service.CheckOnceAsync(Ct));
        _ = service.State;
    }

    // --- the background loop ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_loop_looks_5_seconds_after_the_start_then_every_30_minutes_and_never_sooner()
    {
        var service = Make();
        service.Start([]);

        Time.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(40, Ct);
        Assert.Equal(0, Client.Checks); // not before the 5 seconds

        await Pump(() => Client.Checks == 1, TimeSpan.FromSeconds(1));
        await Pump(() => Client.Checks == 3, TimeSpan.FromMinutes(1));

        var times = Client.CheckTimes;
        var started = new DateTimeOffset(2026, 10, 22, 14, 30, 0, TimeSpan.Zero);
        Assert.True(times[0] - started >= TimeSpan.FromSeconds(5));
        Assert.True(times[0] - started < TimeSpan.FromSeconds(15));
        Assert.True(times[1] - times[0] >= TimeSpan.FromMinutes(30));
        Assert.True(times[2] - times[1] >= TimeSpan.FromMinutes(30));
        Assert.True(times[1] - times[0] < TimeSpan.FromMinutes(32));
    }

    [Fact]
    public async Task A_pc_that_stays_on_for_days_finds_the_release_published_later_and_updates()
    {
        var service = Make();
        service.Start(["--tray"]);
        await Pump(() => Client.Checks == 1, TimeSpan.FromSeconds(1));
        Assert.Empty(Client.Applies);

        Client.Next = Offer("8.1.0"); // published while the app runs
        await Pump(() => Host.Exits == 1, TimeSpan.FromMinutes(1));

        Assert.Equal("8.1.0", Assert.Single(Client.Applies).Offer.Version);
        Assert.Equal(2, Client.Checks);
    }

    [Fact]
    public async Task Offline_the_loop_tries_again_at_the_next_interval_without_throwing()
    {
        Client.CheckFails = new HttpRequestException("offline");
        var service = Make();
        service.Start([]);

        await Pump(() => Client.Checks == 1, TimeSpan.FromSeconds(1));
        await Pump(() => Client.Checks == 3, TimeSpan.FromMinutes(1));

        Assert.Equal(0, Logger.Count(l => l.Level >= LogLevel.Warning));
        Assert.Empty(Client.Applies);
    }

    [Fact]
    public async Task Disposing_stops_the_loop()
    {
        var service = Make();
        service.Start([]);
        service.Dispose();

        Time.Advance(TimeSpan.FromHours(2));
        await Task.Delay(40, Ct);

        Assert.Equal(0, Client.Checks);
    }

    // --- the switch ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_the_switch_off_nothing_happens_by_itself()
    {
        SetAutoUpdate(false);
        Client.Pending = Offer("8.0.9");
        Client.Next = Offer("8.1.0");
        var service = Make();

        Assert.False(service.TryApplyPendingAtStartup(["--tray"]));
        service.Start(["--tray"]);
        Time.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(30, Ct);
        Time.Advance(TimeSpan.FromHours(2));
        await Task.Delay(30, Ct);

        Assert.Equal(UpdateCheckResult.Disabled, await service.CheckOnceAsync(Ct));
        Assert.Equal(0, Client.Checks);
        Assert.Empty(Client.Downloads);
        Assert.Empty(Client.Applies);
        Assert.Equal(0, Host.Exits);
    }

    [Fact]
    public async Task The_manual_check_works_with_the_switch_off_and_installs_when_idle()
    {
        SetAutoUpdate(false);
        Client.Next = Offer("8.1.0");
        var service = Make();
        service.Start(["--tray"]);

        await service.CheckNowAsync(Ct);

        Assert.Equal(["download", "prepare", "apply", "exit"], Calls);
        Assert.Equal(["--tray"], Assert.Single(Client.Applies).Args);
    }

    [Fact]
    public async Task A_manual_check_while_busy_waits_for_the_idle_moment_even_with_the_switch_off()
    {
        SetAutoUpdate(false);
        Host.Idle = false;
        Client.Next = Offer("8.1.0");
        var service = Make();
        service.Start([]);

        await service.CheckNowAsync(Ct);
        Assert.Empty(Client.Applies);

        Host.Idle = true;
        Host.Signal();
        await WaitUntil(() => Host.Exits == 1);
        Assert.Single(Client.Applies);
    }

    [Fact]
    public async Task Turning_the_switch_on_later_lets_the_loop_look_at_the_next_interval()
    {
        SetAutoUpdate(false);
        Client.Next = null;
        var service = Make();
        service.Start([]);
        Time.Advance(TimeSpan.FromMinutes(40));
        await Task.Delay(30, Ct);
        Assert.Equal(0, Client.Checks);

        SetAutoUpdate(true);
        await Pump(() => Client.Checks >= 1, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Turning_the_switch_off_while_an_update_waits_for_idle_stops_the_automatic_apply()
    {
        Host.Idle = false;
        Client.Next = Offer("8.1.0");
        var service = Make();
        service.Start([]);
        await service.CheckOnceAsync(Ct);

        SetAutoUpdate(false);
        Host.Idle = true;
        Host.Signal();
        await Task.Delay(100, Ct);

        Assert.Empty(Client.Applies);
    }

    // --- the update loop guard -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failing_updater_lets_the_app_go_on_and_the_third_attempt_in_24_hours_is_refused()
    {
        Client.Next = Offer("8.1.0");
        Client.ApplyFails = new InvalidOperationException("Update.exe sumiu");
        var service = Make();
        service.Start([]);

        await service.CheckOnceAsync(Ct); // attempt 1
        Host.Signal();
        await WaitUntil(() => FailedInstalls() == 2); // attempt 2
        Assert.Equal(0, Host.Exits);

        Host.Signal(); // would be attempt 3
        await Task.Delay(100, Ct);
        Host.Signal();
        await Task.Delay(100, Ct);

        Assert.Equal(2, Settings.Current.System.UpdateAttempts);
        Assert.Equal(0, Host.Exits);
        Assert.Equal(UpdatePhase.GaveUp, service.State.Phase);
        Assert.Contains("amanh\U000000E3", service.State.Result, StringComparison.Ordinal);
        Assert.Contains("8.1.0", service.State.Result, StringComparison.Ordinal);
        Assert.Equal(1, Logger.Count(l => l.Level == LogLevel.Warning && l.Text.Contains("tentativas", StringComparison.Ordinal))); // once
    }

    [Fact]
    public async Task After_the_day_is_over_the_refused_version_is_tried_again()
    {
        Client.Next = Offer("8.1.0");
        Client.ApplyFails = new InvalidOperationException("Update.exe sumiu");
        var service = Make();
        service.Start([]);
        await service.CheckOnceAsync(Ct);
        Host.Signal();
        await WaitUntil(() => FailedInstalls() == 2);
        Host.Signal();
        await WaitUntil(() => service.State.Phase == UpdatePhase.GaveUp);

        Client.ApplyFails = null; // the updater works again by the time the day is over
        Time.Advance(TimeSpan.FromHours(24));
        Host.Signal();
        await WaitUntil(() => Host.Exits == 1);

        Assert.Single(Client.Applies);
        Assert.Equal(1, Settings.Current.System.UpdateAttempts);
    }

    [Fact]
    public void At_the_start_a_downloaded_update_is_applied_with_the_same_arguments()
    {
        Client.Pending = Offer("8.1.0");
        var service = Make();

        var restarting = service.TryApplyPendingAtStartup(["--tray"]);

        Assert.True(restarting);
        var (offer, args) = Assert.Single(Client.Applies);
        Assert.Equal("8.1.0", offer.Version);
        Assert.Equal(["--tray"], args);
        Assert.Equal(1, Settings.Current.System.UpdateAttempts);
        Assert.False(Settings.HasUnsavedChanges);
    }

    [Fact]
    public void With_nothing_downloaded_the_start_is_left_alone()
    {
        var service = Make();

        Assert.False(service.TryApplyPendingAtStartup([]));
        Assert.Empty(Client.Applies);
    }

    [Fact]
    public void At_the_start_the_same_version_is_applied_twice_in_24_hours_and_then_the_app_just_starts()
    {
        Client.Pending = Offer("8.1.0");

        Assert.True(Make().TryApplyPendingAtStartup(["--tray"]));
        Assert.True(Make().TryApplyPendingAtStartup(["--tray"]));
        var logger = Logger;
        Assert.False(Make().TryApplyPendingAtStartup(["--tray"]));

        Assert.Equal(2, Client.Applies.Count);
        Assert.Contains(logger.Lines, l => l.Level == LogLevel.Warning);

        // A newer download is a new version: tried again at once.
        Client.Pending = Offer("8.2.0");
        Assert.True(Make().TryApplyPendingAtStartup(["--tray"]));
    }

    [Fact]
    public void A_failing_updater_at_the_start_lets_the_app_start_normally()
    {
        Client.Pending = Offer("8.1.0");
        Client.ApplyFails = new InvalidOperationException("Update.exe sumiu");
        var service = Make();

        Assert.False(service.TryApplyPendingAtStartup(["--tray"]));
    }

    // --- the real client -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_feed_includes_pre_releases_and_it_is_one_constant_to_change()
    {
        Assert.True(VelopackUpdateClient.IncludePreReleases);

        var source = VelopackUpdateClient.CreateSource(VelopackUpdateClient.RepositoryUrl);

        Assert.True(source.Prerelease);
        Assert.False(VelopackUpdateClient.CreateSource(VelopackUpdateClient.RepositoryUrl, includePreReleases: false).Prerelease);
    }

    [Fact]
    public async Task The_real_client_in_a_copy_that_is_not_installed_is_a_quiet_no_op()
    {
        var client = new VelopackUpdateClient();
        var service = new UpdateService(client, Host, Settings, Logger, Time);
        _disposables.Add(service);

        Assert.False(client.IsInstalled);
        Assert.Null(client.Pending);
        Assert.Equal(UpdateCheckResult.NotInstalled, await service.CheckOnceAsync(Ct));
        Assert.False(service.TryApplyPendingAtStartup(["--tray"]));
        Assert.Matches(@"^\d+\.\d+\.\d+", client.CurrentVersion);
        Assert.Equal("https://github.com/AndreyGutinik/Alerta-IBT-2026", VelopackUpdateClient.RepositoryUrl);
    }

    // --- the marker that lets the relaunched app wait for the old one ---------------------------------------------------

    [Fact]
    public void A_fresh_restart_marker_makes_the_new_start_wait_for_the_lock_and_is_used_up()
    {
        var file = Path.Combine(_dir, "update-restart.txt");
        UpdateRestartMarker.Write(file, Time.GetUtcNow());

        Assert.Equal(UpdateRestartMarker.WaitForLock, UpdateRestartMarker.ConsumeWait(file, Time.GetUtcNow().AddSeconds(8)));
        Assert.False(File.Exists(file));
        Assert.Equal(TimeSpan.Zero, UpdateRestartMarker.ConsumeWait(file, Time.GetUtcNow().AddSeconds(8)));
    }

    [Theory]
    [InlineData("old")]
    [InlineData("garbage")]
    [InlineData("missing")]
    public void A_stale_damaged_or_missing_marker_means_no_wait(string kind)
    {
        var file = Path.Combine(_dir, "update-restart.txt");
        if (kind == "old")
        {
            UpdateRestartMarker.Write(file, Time.GetUtcNow());
        }
        else if (kind == "garbage")
        {
            File.WriteAllText(file, "not a time");
        }

        Assert.Equal(TimeSpan.Zero, UpdateRestartMarker.ConsumeWait(file, Time.GetUtcNow().AddMinutes(10)));
    }

    [Fact]
    public async Task The_marker_is_written_before_the_updater_is_started()
    {
        var file = Path.Combine(_dir, "update-restart.txt");
        Client.Next = Offer("8.1.0");
        var service = new UpdateService(Client, Host, Settings, Logger, Time, restartMarkerFile: file);
        _disposables.Add(service);
        service.Start([]);

        await service.CheckOnceAsync(Ct);

        Assert.True(File.Exists(file));
    }

    private sealed class ThrowingClient : IUpdateClient
    {
        public bool IsInstalled => throw new InvalidOperationException("boom");

        public string CurrentVersion => throw new InvalidOperationException("boom");

        public UpdateOffer? Pending => throw new InvalidOperationException("boom");

        public Task<UpdateOffer?> CheckAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("boom");

        public Task DownloadAsync(UpdateOffer offer, IProgress<int>? progress, CancellationToken cancellationToken) => throw new InvalidOperationException("boom");

        public void ApplyAndRestart(UpdateOffer offer, string[] restartArguments) => throw new InvalidOperationException("boom");
    }
}

using System.Net.Http;
using AlertaIBT.App.Platform;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.App.Tests.Platform;

public sealed class UpdateServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-update-" + Guid.NewGuid().ToString("N"));

    private string AttemptFile => Path.Combine(_dir, "update-attempt.txt");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
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
    }

    private sealed class FakeClient : IUpdateClient
    {
        public bool IsInstalled { get; set; } = true;

        public string CurrentVersion { get; set; } = "8.0.0";

        public UpdateOffer? Pending { get; set; }

        public UpdateOffer? Next { get; set; }

        public Exception? CheckFails { get; set; }

        public Exception? DownloadFails { get; set; }

        public Exception? ApplyFails { get; set; }

        public bool CheckHangsUntilCancelled { get; set; }

        public int Checks;
        public List<UpdateOffer> Downloads { get; } = [];
        public List<(UpdateOffer Offer, string[] Args)> Applies { get; } = [];
        public TaskCompletionSource FirstCheck { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<UpdateOffer?> CheckAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Checks);
            FirstCheck.TrySetResult();
            if (CheckHangsUntilCancelled)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return CheckFails is not null ? throw CheckFails : Next;
        }

        public Task DownloadAsync(UpdateOffer offer, CancellationToken cancellationToken)
        {
            if (DownloadFails is not null)
            {
                throw DownloadFails;
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

            Applies.Add((offer, restartArguments));
        }
    }

    private UpdateService Service(FakeClient client, ListLogger? logger = null, Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? checkTimeout = null) =>
        new(client, logger ?? new ListLogger(), AttemptFile, delay ?? ((_, _) => Task.CompletedTask), checkTimeout: checkTimeout);

    private static UpdateOffer Offer(string version) => new(version, version);

    [Fact]
    public async Task A_copy_that_is_not_installed_never_asks_the_feed()
    {
        var client = new FakeClient { IsInstalled = false, Next = Offer("9.0.0") };
        using var service = Service(client);

        Assert.Equal(UpdateCheckResult.NotInstalled, await service.CheckOnceAsync(TestContext.Current.CancellationToken));
        service.Start();
        Assert.False(service.TryApplyPendingAtStartup(["--tray"]));
        Assert.Equal(0, client.Checks);
        Assert.Empty(client.Downloads);
    }

    [Fact]
    public async Task No_newer_release_means_up_to_date_and_no_download()
    {
        var client = new FakeClient { Next = null };
        using var service = Service(client);

        Assert.Equal(UpdateCheckResult.UpToDate, await service.CheckOnceAsync(TestContext.Current.CancellationToken));
        Assert.Empty(client.Downloads);
    }

    [Fact]
    public async Task A_newer_release_is_downloaded_once_and_never_applied_by_the_check()
    {
        var client = new FakeClient { Next = Offer("8.1.0") };
        using var service = Service(client);

        Assert.Equal(UpdateCheckResult.Downloaded, await service.CheckOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal("8.1.0", Assert.Single(client.Downloads).Version);
        Assert.Empty(client.Applies); // never restarts the app while it serves
    }

    [Fact]
    public async Task A_release_already_downloaded_is_not_downloaded_again()
    {
        var client = new FakeClient { Next = Offer("8.1.0"), Pending = Offer("8.1.0") };
        using var service = Service(client);

        Assert.Equal(UpdateCheckResult.Downloaded, await service.CheckOnceAsync(TestContext.Current.CancellationToken));
        Assert.Empty(client.Downloads);
    }

    [Theory]
    [InlineData("check")]
    [InlineData("download")]
    public async Task Offline_is_a_quiet_failure_not_an_exception_and_not_an_error_line(string where)
    {
        var client = new FakeClient { Next = Offer("8.1.0") };
        if (where == "check")
        {
            client.CheckFails = new HttpRequestException("Nenhuma conexao");
        }
        else
        {
            client.DownloadFails = new HttpRequestException("Nenhuma conexao");
        }

        var logger = new ListLogger();
        using var service = Service(client, logger);

        Assert.Equal(UpdateCheckResult.Failed, await service.CheckOnceAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(logger.Lines, l => l.Level >= LogLevel.Warning);
        Assert.Contains(logger.Lines, l => l.Text.Contains("Nenhuma conexao", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_feed_that_never_answers_ends_in_failure_after_the_timeout()
    {
        var client = new FakeClient { CheckHangsUntilCancelled = true };
        using var service = Service(client, checkTimeout: TimeSpan.FromMilliseconds(60));

        var result = await service.CheckOnceAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckResult.Failed, result);
    }

    [Fact]
    public async Task Cancelling_the_check_from_outside_is_honoured()
    {
        var client = new FakeClient { CheckHangsUntilCancelled = true };
        using var service = Service(client);
        using var cts = new CancellationTokenSource();
        var running = service.CheckOnceAsync(cts.Token);

        await client.FirstCheck.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public void A_downloaded_update_is_applied_at_the_next_start_with_the_same_arguments()
    {
        var client = new FakeClient { Pending = Offer("8.1.0") };
        using var service = Service(client);

        var restarting = service.TryApplyPendingAtStartup(["--tray"]);

        Assert.True(restarting);
        var (offer, args) = Assert.Single(client.Applies);
        Assert.Equal("8.1.0", offer.Version);
        Assert.Equal(["--tray"], args);
    }

    [Fact]
    public void With_nothing_downloaded_the_start_is_left_alone()
    {
        var client = new FakeClient { Pending = null };
        using var service = Service(client);

        Assert.False(service.TryApplyPendingAtStartup([]));
        Assert.Empty(client.Applies);
    }

    [Fact]
    public void A_version_whose_apply_did_not_work_is_not_retried_at_every_start()
    {
        var client = new FakeClient { Pending = Offer("8.1.0") };
        using (var first = Service(client))
        {
            Assert.True(first.TryApplyPendingAtStartup(["--tray"]));
        }

        // The app is running again (the apply did not replace it) and the same update is still waiting.
        var logger = new ListLogger();
        using (var second = Service(client, logger))
        {
            Assert.False(second.TryApplyPendingAtStartup(["--tray"]));
        }

        Assert.Single(client.Applies);
        Assert.Contains(logger.Lines, l => l.Level == LogLevel.Warning);

        // A newer download is tried again.
        client.Pending = Offer("8.2.0");
        using var third = Service(client);
        Assert.True(third.TryApplyPendingAtStartup(["--tray"]));
        Assert.Equal(2, client.Applies.Count);
    }

    [Fact]
    public void A_failing_updater_lets_the_app_start_normally()
    {
        var client = new FakeClient { Pending = Offer("8.1.0"), ApplyFails = new InvalidOperationException("Update.exe sumiu") };
        using var service = Service(client);

        Assert.False(service.TryApplyPendingAtStartup(["--tray"]));
    }

    [Fact]
    public async Task The_background_loop_checks_after_the_first_wait_and_stops_once_it_has_an_update()
    {
        var client = new FakeClient { Next = Offer("8.1.0") };
        var waits = new List<TimeSpan>();
        using var service = new UpdateService(
            client,
            new ListLogger(),
            AttemptFile,
            (span, _) =>
            {
                lock (waits)
                {
                    waits.Add(span);
                }

                return Task.CompletedTask;
            },
            firstCheckDelay: TimeSpan.FromMinutes(2),
            interval: TimeSpan.FromHours(6));

        service.Start();
        await client.FirstCheck.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await WaitUntil(() => client.Downloads.Count == 1);

        await Task.Delay(100, TestContext.Current.CancellationToken); // a second check would show up here
        Assert.Equal(1, Volatile.Read(ref client.Checks));
        lock (waits)
        {
            Assert.Equal([TimeSpan.FromMinutes(2)], waits); // only the first wait: no interval after a download
        }
    }

    [Fact]
    public async Task The_background_loop_keeps_trying_every_interval_while_offline_and_stops_on_dispose()
    {
        var client = new FakeClient { CheckFails = new HttpRequestException("offline") };
        var intervals = 0;
        var gate = new SemaphoreSlim(0);
        using var service = new UpdateService(
            client,
            new ListLogger(),
            AttemptFile,
            async (span, token) =>
            {
                if (span == TimeSpan.FromHours(6) && Interlocked.Increment(ref intervals) >= 3)
                {
                    gate.Release();
                    await Task.Delay(Timeout.Infinite, token); // the fourth wait never ends
                }
            },
            firstCheckDelay: TimeSpan.FromMinutes(2),
            interval: TimeSpan.FromHours(6));

        service.Start();
        await gate.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(3, Volatile.Read(ref client.Checks));
        service.Dispose();
    }

    [Fact]
    public void Start_returns_at_once_and_never_throws_even_if_the_client_blows_up()
    {
        using var service = new UpdateService(new ThrowingClient(), new ListLogger(), AttemptFile);

        service.Start();
        Assert.False(service.TryApplyPendingAtStartup([]));
    }

    [Fact]
    public async Task The_real_client_in_a_copy_that_is_not_installed_is_a_quiet_no_op()
    {
        var client = new VelopackUpdateClient();
        using var service = new UpdateService(client, new ListLogger(), AttemptFile);

        Assert.False(client.IsInstalled);
        Assert.Null(client.Pending);
        Assert.Equal(UpdateCheckResult.NotInstalled, await service.CheckOnceAsync(TestContext.Current.CancellationToken));
        Assert.False(service.TryApplyPendingAtStartup(["--tray"]));
        Assert.Matches(@"^\d+\.\d+\.\d+", client.CurrentVersion);
        Assert.Equal("https://github.com/AndreyGutinik/Alerta-IBT-2026", VelopackUpdateClient.RepositoryUrl);
    }

    private sealed class ThrowingClient : IUpdateClient
    {
        public bool IsInstalled => throw new InvalidOperationException("boom");

        public string CurrentVersion => throw new InvalidOperationException("boom");

        public UpdateOffer? Pending => throw new InvalidOperationException("boom");

        public Task<UpdateOffer?> CheckAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("boom");

        public Task DownloadAsync(UpdateOffer offer, CancellationToken cancellationToken) => throw new InvalidOperationException("boom");

        public void ApplyAndRestart(UpdateOffer offer, string[] restartArguments) => throw new InvalidOperationException("boom");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "condition was not met in time");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}

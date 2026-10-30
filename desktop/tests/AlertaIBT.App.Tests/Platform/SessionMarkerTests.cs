using System.Windows.Threading;
using AlertaIBT.App.Platform;
using AlertaIBT.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.App.Tests.Platform;

// How the next start learns that the previous run died: the marker file, and the watchdog on a real dispatcher thread.
public sealed class SessionMarkerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-session-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string File => Path.Combine(_dir, "session.txt");

    [Fact]
    public void The_first_start_finds_nothing_and_is_not_abnormal()
    {
        var previous = new SessionMarker(File).Begin("8.0.4");

        Assert.False(previous.Existed);
        Assert.False(previous.EndedAbnormally);
    }

    [Fact]
    public void A_run_that_wrote_its_end_line_is_clean_and_keeps_its_reason()
    {
        var first = new SessionMarker(File);
        first.Begin("8.0.4-pre.1");
        first.End("Windows encerrando a sessao (Shutdown)");

        var previous = new SessionMarker(File).Begin("8.0.4-pre.2");

        Assert.True(previous.Existed);
        Assert.True(previous.EndedCleanly);
        Assert.False(previous.EndedAbnormally);
        Assert.Equal("8.0.4-pre.1", previous.Version);
        Assert.Equal("Windows encerrando a sessao (Shutdown)", previous.EndReason);
        Assert.NotNull(previous.StartedAt);
    }

    [Fact]
    public void A_run_that_never_wrote_its_end_line_is_reported_as_abnormal_once()
    {
        new SessionMarker(File).Begin("8.0.4-pre.1"); // killed: no End call

        var next = new SessionMarker(File);
        var previous = next.Begin("8.0.4-pre.2");
        Assert.True(previous.EndedAbnormally);
        Assert.Equal("8.0.4-pre.1", previous.Version);

        next.End("saida normal");
        Assert.False(new SessionMarker(File).Begin("8.0.4-pre.3").EndedAbnormally); // only the run that died is reported, not the ones after
    }

    [Fact]
    public void A_marker_that_cannot_be_written_never_throws()
    {
        var blocked = Path.Combine(_dir, "blocked");
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(blocked, "a file where the folder should be");

        var marker = new SessionMarker(Path.Combine(blocked, "session.txt"));
        var previous = marker.Begin("8.0.4");
        marker.End("saida normal");

        Assert.False(previous.EndedAbnormally);
    }

    [Fact]
    public void The_process_counters_are_text_and_never_throw()
    {
        var text = ProcessCounters.Describe();

        Assert.Contains("GDI", text, StringComparison.Ordinal);
        Assert.Contains("USER", text, StringComparison.Ordinal);
        Assert.Contains("threads", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_watchdog_on_a_real_dispatcher_logs_a_UI_thread_that_is_blocked_for_more_than_two_seconds_and_its_recovery()
    {
        var log = new ListLogger();
        Dispatcher? dispatcher = null;
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "watchdog test UI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        var trail = new BreadcrumbTrail();
        trail.Add("player: Play");
        using var watchdog = new UiStallWatchdog(action => dispatcher!.BeginInvoke(DispatcherPriority.Input, action), log, trail: trail, context: ProcessCounters.Describe);
        watchdog.Start();
        Thread.Sleep(600); // a few healthy pings first
        Assert.Empty(log.Messages);

        dispatcher!.BeginInvoke(() => Thread.Sleep(2800)); // the "freeze"
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(8) && !log.Messages.Any(m => m.Contains("voltou a responder", StringComparison.Ordinal)))
        {
            Thread.Sleep(50);
        }

        dispatcher.InvokeShutdown();
        Assert.Contains(log.Messages, m => m.StartsWith("Interface parada ha ", StringComparison.Ordinal) && m.Contains("player: Play", StringComparison.Ordinal) && m.Contains("GDI", StringComparison.Ordinal));
        Assert.Contains(log.Messages, m => m.StartsWith("Interface voltou a responder", StringComparison.Ordinal));
        Assert.InRange(watchdog.LongestStall.TotalSeconds, 2.0, 8.0);
    }

    private sealed class ListLogger : ILogger
    {
        private readonly object _gate = new();
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_gate)
                {
                    return [.. _messages];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }
}

using System.Collections.Concurrent;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Services;

internal static class Wait
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    public static async Task Until(Func<bool> condition, string what = "the condition")
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), what + " was not met in time");
    }

    // Lets the background loops run to quiet: used to prove that something does NOT happen.
    public static async Task Settle() => await Task.Delay(80, TestContext.Current.CancellationToken);
}

/// <summary>A bar that plays what the test tells it to, like the overlay controller does (a cancel ends the pass with false).</summary>
internal sealed class FakeDisplay : IAlertDisplay
{
    private readonly object _gate = new();
    private bool _led = true;

    public sealed class Pass(string text)
    {
        private readonly TaskCompletionSource<bool> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Text { get; } = text;

        public Task<bool> Task => _done.Task;

        public bool Cancelled { get; set; }

        public void Complete(bool passed) => _done.TrySetResult(passed);
    }

    public bool LedPresent
    {
        get
        {
            lock (_gate)
            {
                return _led;
            }
        }
    }

    public event Action<bool>? LedPresenceChanged;

    public List<Pass> Passes { get; } = [];

    public Exception? ThrowOnShow { get; set; }

    /// <summary>When set, every pass ends at once with this result (a bar that keeps failing, or one that is instantaneous).</summary>
    public bool? AutoResult { get; set; }

    /// <summary>When false, a cancel does not end the pass (a bar slow to react): the test completes it by hand.</summary>
    public bool CancelEndsPass { get; set; } = true;

    public Pass? Current
    {
        get
        {
            lock (_gate)
            {
                return Passes.LastOrDefault(p => !p.Task.IsCompleted);
            }
        }
    }

    public string[] Texts
    {
        get
        {
            lock (_gate)
            {
                return Passes.Select(p => p.Text).ToArray();
            }
        }
    }

    public Task<bool> ShowAsync(string text, CancellationToken ct)
    {
        lock (_gate)
        {
            if (ThrowOnShow is { } error)
            {
                ThrowOnShow = null;
                throw error;
            }
        }

        var pass = new Pass(text);
        lock (_gate)
        {
            Passes.Add(pass);
        }

        ct.Register(() =>
        {
            pass.Cancelled = true;
            if (CancelEndsPass)
            {
                pass.Complete(false);
            }
        });
        if (AutoResult is { } auto)
        {
            pass.Complete(auto);
        }

        return pass.Task;
    }

    // The LED appears or vanishes: the event first, then the pass in progress is cut short, the worst order for the service.
    public void SetLed(bool present)
    {
        Pass? cut = null;
        lock (_gate)
        {
            _led = present;
            if (!present)
            {
                cut = Passes.LastOrDefault(p => !p.Task.IsCompleted);
            }
        }

        LedPresenceChanged?.Invoke(present);
        cut?.Complete(false);
    }

    public async Task<Pass> NextPass(int count)
    {
        await Wait.Until(() => Texts.Length >= count, $"pass #{count}");
        lock (_gate)
        {
            return Passes[count - 1];
        }
    }
}

internal sealed class FakeNotifier : IGroupNotifier
{
    private readonly ConcurrentQueue<string> _sent = new();

    public string[] Sent => _sent.ToArray();

    public void Notify(string text) => _sent.Enqueue(text);
}

internal sealed class FakeCorrector(Func<string, CancellationToken, Task<ParsedAlert?>> run) : ICorrector
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Task<ParsedAlert?> CorrectAsync(string raw, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        return run(raw, ct);
    }
}

internal sealed class CapturingLogger : ILogger
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    public (LogLevel Level, string Message)[] Entries => _entries.ToArray();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _entries.Enqueue((logLevel, formatter(state, exception)));
}

/// <summary>Everything an <see cref="AlertService"/> needs, with the Gemini corrector and the bar under the test's control.</summary>
internal sealed class ServiceHarness : IDisposable
{
    private int _ids;

    public ServiceHarness(Func<string, CancellationToken, Task<ParsedAlert?>>? gemini = null, bool start = true, AlertQueue? queue = null)
    {
        SettingsProvider = () => Settings;
        Gemini = new FakeCorrector(gemini ?? ((_, _) => Task.FromResult<ParsedAlert?>(null)));
        Offline = new FakeCorrector((raw, _) => Task.FromResult<ParsedAlert?>(AlertParser.Parse(raw)));
        Pipeline = new CorrectionPipeline(Gemini, Offline, () => FixedRules.Empty, TimeSpan.FromSeconds(3), Clock);
        Queue = queue ?? new AlertQueue(Clock);
        Service = new AlertService(
            Queue,
            Pipeline,
            Display,
            Notifier,
            () => SettingsProvider(),
            Hub,
            Logger,
            Clock,
            () => "id" + Interlocked.Increment(ref _ids));
        if (start)
        {
            Service.Start();
        }
    }

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));

    public AppSettings Settings { get; set; } = new();

    public Func<AppSettings> SettingsProvider { get; set; } = null!;

    public FakeDisplay Display { get; } = new();

    public FakeNotifier Notifier { get; } = new();

    public StatusHub Hub { get; } = new();

    public CapturingLogger Logger { get; } = new();

    public FakeCorrector Gemini { get; }

    public FakeCorrector Offline { get; }

    public CorrectionPipeline Pipeline { get; }

    public AlertQueue Queue { get; }

    public AlertService Service { get; }

    public Task<SubmitOutcome> Telegram(string raw) =>
        Service.SubmitAsync(raw, AlertSource.Telegram, SenderRole.Member, false, "42", TestContext.Current.CancellationToken);

    public Task<SubmitOutcome> Pc(string raw, bool urgent = false) =>
        Service.SubmitAsync(raw, AlertSource.Pc, SenderRole.Operator, urgent, null, TestContext.Current.CancellationToken);

    public void Dispose() => Service.Dispose();
}

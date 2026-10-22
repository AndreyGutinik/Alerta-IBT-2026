using System.Net;
using System.Text;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Services;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Services;

// When the Gemini probe runs (start, settings change, every 30 minutes while idle), when it does not, and what the dot shows.
public sealed class GeminiHealthMonitorTests : IDisposable
{
    private const string Key = "AIzaSyMONITORKEY-0123456789abcdefghi";
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));
    private readonly StatusHub _hub;
    private readonly List<bool> _health = [];
    private readonly List<GeminiHealthMonitor> _monitors = [];
    private GeminiProbeSettings _settings = new(Enabled: true, GeminiMode: true, Key, "gemini-test");

    public GeminiHealthMonitorTests() => _hub = new StatusHub(_clock);

    public void Dispose()
    {
        foreach (var monitor in _monitors)
        {
            monitor.Dispose();
        }
    }

    // ---- fakes ----

    private sealed class FakeProbe(Func<int, GeminiProbeResult> answer, FakeTimeProvider clock) : IGeminiHealthProbe
    {
        public List<DateTimeOffset> Calls { get; } = [];

        // One gate per call, in order: a call with a gate waits for it; the calls after the last gate do not wait.
        public List<TaskCompletionSource> Gates { get; } = [];

        public async Task<GeminiProbeResult> ProbeAsync(CancellationToken ct)
        {
            Calls.Add(clock.GetUtcNow());
            if (Gates.Count > 0)
            {
                var gate = Gates[0];
                Gates.RemoveAt(0);
                await gate.Task.ConfigureAwait(false);
            }

            return answer(Calls.Count);
        }
    }

    private static GeminiProbeResult Online() =>
        new(GeminiProbeStatus.Online, GeminiFailureKind.None, string.Empty, null, TimeSpan.FromMilliseconds(120));

    private static GeminiProbeResult Failed(string reason = "tempo esgotado") =>
        new(GeminiProbeStatus.Failed, GeminiFailureKind.Timeout, reason, null, TimeSpan.FromSeconds(8));

    private CorrectionPipeline Pipeline(ICorrector? gemini = null)
    {
        var pipeline = new CorrectionPipeline(
            gemini ?? new FakeCorrector((raw, _) => Task.FromResult<ParsedAlert?>(AlertParser.Parse(raw))),
            new FakeCorrector((raw, _) => Task.FromResult<ParsedAlert?>(AlertParser.Parse(raw))),
            () => FixedRules.Empty,
            TimeSpan.FromSeconds(5),
            _clock);
        pipeline.GeminiHealthChanged += _health.Add;
        pipeline.GeminiHealthChanged += _hub.SetGemini;
        return pipeline;
    }

    private GeminiHealthMonitor Monitor(IGeminiHealthProbe probe, CorrectionPipeline pipeline)
    {
        var monitor = new GeminiHealthMonitor(probe, pipeline, _hub, () => _settings, _clock);
        _monitors.Add(monitor);
        monitor.Start();
        return monitor;
    }

    private sealed class FakeCorrector(Func<string, CancellationToken, Task<ParsedAlert?>> run) : ICorrector
    {
        public Task<ParsedAlert?> CorrectAsync(string raw, CancellationToken ct) => run(raw, ct);
    }

    // ---- start ----

    [Fact]
    public void FirstProbeComesEightSecondsAfterStart_AndTurnsTheDotGreen()
    {
        var probe = new FakeProbe(_ => Online(), _clock);
        Monitor(probe, Pipeline());

        _clock.Advance(7 * Second);
        Assert.Empty(probe.Calls);
        Assert.Equal(StatusLight.Gray, _hub.Gemini.Light);

        _clock.Advance(Second);

        Assert.Single(probe.Calls);
        Assert.Equal(StatusLight.Green, _hub.Gemini.Light);
        Assert.Equal("Gemini conectado agora", _hub.Gemini.Detail);
        Assert.Equal([true], _health);
    }

    [Fact]
    public void WhileTheFirstProbeIsInFlight_TheDotIsYellowVerificando()
    {
        var gate = new TaskCompletionSource();
        var probe = new FakeProbe(_ => Online(), _clock) { Gates = { gate } };
        Monitor(probe, Pipeline());

        _clock.Advance(8 * Second);

        Assert.Equal((StatusLight.Yellow, "Verificando"), (_hub.Gemini.Light, _hub.Gemini.Text));

        gate.SetResult();

        Assert.Equal(StatusLight.Green, _hub.Gemini.Light);
    }

    [Fact]
    public void AFailedProbe_TurnsTheDotBlueWithTheReasonAndTheAge()
    {
        var probe = new FakeProbe(_ => Failed("HTTP 403 PERMISSION_DENIED: API key not valid."), _clock);
        Monitor(probe, Pipeline());

        _clock.Advance(8 * Second);

        Assert.Equal((StatusLight.Blue, "Offline"), (_hub.Gemini.Light, _hub.Gemini.Text));
        Assert.StartsWith("Offline: HTTP 403 PERMISSION_DENIED: API key not valid. (verificado agora)", _hub.Gemini.Detail, StringComparison.Ordinal);
        Assert.Equal([false], _health);
    }

    [Fact]
    public void TheTooltipAgeMovesOnEveryMinute()
    {
        var probe = new FakeProbe(_ => Online(), _clock);
        Monitor(probe, Pipeline());
        _clock.Advance(8 * Second);
        var raised = new List<string>();
        _hub.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        _clock.Advance(TimeSpan.FromMinutes(3));

        Assert.Equal("Gemini conectado h\U000000E1 3 min", _hub.Gemini.Detail);
        Assert.Contains("Gemini", raised);
    }

    // ---- the schedule ----

    [Fact]
    public void WhileAllIsWell_ProbesRepeatEveryThirtyMinutes()
    {
        var probe = new FakeProbe(_ => Online(), _clock);
        Monitor(probe, Pipeline());

        _clock.Advance(8 * Second);
        _clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Single(probe.Calls);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, probe.Calls.Count);
        _clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(3, probe.Calls.Count);
        Assert.Equal(TimeSpan.FromMinutes(30), probe.Calls[2] - probe.Calls[1]);
    }

    [Fact]
    public void Backoff_Doubles_FromThirtySecondsUpToThirtyMinutes()
    {
        var probe = new FakeProbe(_ => Failed(), _clock);
        Monitor(probe, Pipeline());

        for (var i = 0; i < 4 * 3600; i++)
        {
            _clock.Advance(Second); // a second at a time: the fake clock fires a timer once per Advance
        }

        var gaps = probe.Calls.Zip(probe.Calls.Skip(1), (a, b) => b - a).ToArray();
        Assert.Equal(
            [
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(2),
                TimeSpan.FromMinutes(4),
                TimeSpan.FromMinutes(8),
                TimeSpan.FromMinutes(16),
                TimeSpan.FromMinutes(30),
            ],
            gaps.Take(7));
        Assert.All(gaps, gap => Assert.True(gap <= TimeSpan.FromMinutes(30)));
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(6, 960)]
    [InlineData(7, 1800)]
    [InlineData(40, 1800)]
    public void BackoffFor_FollowsTheSchedule(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), GeminiHealthMonitor.BackoffFor(failures));

    [Fact]
    public void ARecoveryClearsTheBackoff_AndGoesBackToThirtyMinutes()
    {
        var probe = new FakeProbe(n => n <= 2 ? Failed() : Online(), _clock);
        Monitor(probe, Pipeline());

        _clock.Advance(8 * Second);
        _clock.Advance(30 * Second);
        _clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(3, probe.Calls.Count);
        Assert.Equal(StatusLight.Green, _hub.Gemini.Light);
        Assert.Equal([false, true], _health);

        _clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(3, probe.Calls.Count);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(4, probe.Calls.Count);
    }

    // ---- real corrections are probes ----

    [Fact]
    public async Task ARealCorrectionWithinTwentyMinutes_SuppressesThePeriodicProbe()
    {
        var probe = new FakeProbe(_ => Online(), _clock);
        var pipeline = Pipeline();
        Monitor(probe, pipeline);
        _clock.Advance(8 * Second);
        _clock.Advance(TimeSpan.FromMinutes(15));
        await pipeline.RunAsync("gol branco abc1234 farol aceso", useGemini: true, TestContext.Current.CancellationToken);

        _clock.Advance(TimeSpan.FromMinutes(15)); // 30 minutes after the probe, 15 after the real call

        Assert.Single(probe.Calls);

        _clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Single(probe.Calls);
        _clock.Advance(TimeSpan.FromMinutes(1)); // 20 minutes after the real call

        Assert.Equal(2, probe.Calls.Count);
    }

    [Fact]
    public async Task ARealCorrectionOlderThanTwentyMinutes_DoesNotSuppressIt()
    {
        var probe = new FakeProbe(_ => Online(), _clock);
        var pipeline = Pipeline();
        Monitor(probe, pipeline);
        _clock.Advance(8 * Second);
        await pipeline.RunAsync("gol branco abc1234 farol aceso", useGemini: true, TestContext.Current.CancellationToken);

        _clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(2, probe.Calls.Count);
    }

    [Fact]
    public async Task ARealCall_MovesTheCheckedTimeOfTheTooltip()
    {
        var probe = new FakeProbe(_ => Online(), _clock);
        var pipeline = Pipeline();
        Monitor(probe, pipeline);
        _clock.Advance(8 * Second);
        _clock.Advance(TimeSpan.FromMinutes(10));
        await pipeline.RunAsync("gol branco abc1234 farol aceso", useGemini: true, TestContext.Current.CancellationToken);

        _clock.Advance(TimeSpan.FromMinutes(2)); // the age tick

        Assert.Equal("Gemini conectado h\U000000E1 2 min", _hub.Gemini.Detail);
    }

    [Fact]
    public void AProbeThatDisagreesWithTheLastRealCall_IsAnnouncedAndTheNextRealCallToo()
    {
        var probe = new FakeProbe(n => n == 1 ? Online() : Failed(), _clock);
        var pipeline = Pipeline();
        Monitor(probe, pipeline);

        _clock.Advance(8 * Second);
        _clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal([true, false], _health);
        Assert.Equal(StatusLight.Blue, _hub.Gemini.Light);
    }

    // ---- skips ----

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void CorrectionOffOrNotInGeminiMode_NeverProbes(bool enabled, bool geminiMode)
    {
        _settings = new GeminiProbeSettings(enabled, geminiMode, Key, "gemini-test");
        var probe = new FakeProbe(_ => Online(), _clock);
        Monitor(probe, Pipeline());

        _clock.Advance(TimeSpan.FromHours(2));

        Assert.Empty(probe.Calls);
    }

    [Fact]
    public void NoKey_NeverProbes_AndTheDotSaysSemChave()
    {
        _settings = _settings with { Key = null };
        var probe = new FakeProbe(_ => Online(), _clock);
        Monitor(probe, Pipeline());

        _clock.Advance(TimeSpan.FromHours(2));

        Assert.Empty(probe.Calls);
        Assert.Equal((StatusLight.Gray, "Sem chave"), (_hub.Gemini.Light, _hub.Gemini.Text));
        Assert.Equal("Chave do Gemini n\U000000E3o configurada", _hub.Gemini.Detail);
    }

    // ---- settings changes ----

    [Fact]
    public void ChangingTheKeyThreeTimesInARow_ProbesOnceAfterTheDebounce()
    {
        var probe = new FakeProbe(_ => Online(), _clock);
        var monitor = Monitor(probe, Pipeline());
        _clock.Advance(8 * Second);
        Assert.Single(probe.Calls);

        foreach (var key in new[] { "k1", "k2", "k3" })
        {
            _settings = _settings with { Key = key };
            monitor.OnSettingsChanged();
            _clock.Advance(Second);
        }

        Assert.Single(probe.Calls);
        _clock.Advance(2 * Second); // 3 s after the last change

        Assert.Equal(2, probe.Calls.Count);
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(2, probe.Calls.Count);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("model")]
    [InlineData("enabled")]
    [InlineData("mode")]
    public void ChangingKeyModelOrTheSwitch_ProbesAgain(string what)
    {
        var probe = new FakeProbe(_ => Online(), _clock);
        var monitor = Monitor(probe, Pipeline());
        _clock.Advance(8 * Second);
        _settings = what switch
        {
            "key" => _settings with { Key = "other" },
            "model" => _settings with { Model = "gemini-other" },
            "enabled" => _settings with { Enabled = false },
            _ => _settings with { GeminiMode = false },
        };
        monitor.OnSettingsChanged();
        if (what is "enabled" or "mode")
        {
            _clock.Advance(5 * Second);
            Assert.Single(probe.Calls); // switched off: nothing to ask
            _settings = new GeminiProbeSettings(true, true, Key, "gemini-test");
            monitor.OnSettingsChanged();
        }

        _clock.Advance(3 * Second);

        Assert.Equal(2, probe.Calls.Count);
    }

    [Fact]
    public void ASaveThatChangesNothingTheProbeCaresAbout_DoesNotProbe()
    {
        var probe = new FakeProbe(_ => Online(), _clock);
        var monitor = Monitor(probe, Pipeline());
        _clock.Advance(8 * Second);

        monitor.OnSettingsChanged();
        _clock.Advance(10 * Second);

        Assert.Single(probe.Calls);
    }

    [Fact]
    public void SettingTheKeyAfterStart_ProbesAndTheDotLeavesSemChave()
    {
        _settings = _settings with { Key = null };
        var probe = new FakeProbe(_ => Online(), _clock);
        var monitor = Monitor(probe, Pipeline());
        _clock.Advance(10 * Second);
        Assert.Equal("Sem chave", _hub.Gemini.Text);

        _settings = _settings with { Key = Key };
        monitor.OnSettingsChanged();
        _clock.Advance(3 * Second);

        Assert.Single(probe.Calls);
        Assert.Equal(StatusLight.Green, _hub.Gemini.Light);
    }

    [Fact]
    public void ASettingsChangeWhileAFailingProbeBacksOff_StartsTheBackoffOver()
    {
        var probe = new FakeProbe(_ => Failed(), _clock);
        var monitor = Monitor(probe, Pipeline());
        _clock.Advance(8 * Second);
        _clock.Advance(30 * Second);
        _clock.Advance(TimeSpan.FromMinutes(1)); // three failures: the next wait is 2 minutes

        _settings = _settings with { Key = "fixed-key" };
        monitor.OnSettingsChanged();
        _clock.Advance(3 * Second);
        var calls = probe.Calls.Count;
        _clock.Advance(30 * Second);

        Assert.Equal(calls + 1, probe.Calls.Count);
    }

    [Fact]
    public void AResultThatArrivesAfterASettingsChange_IsDiscarded()
    {
        var gate = new TaskCompletionSource();
        var probe = new FakeProbe(_ => Failed("old key"), _clock) { Gates = { gate } };
        var monitor = Monitor(probe, Pipeline());
        _clock.Advance(8 * Second);
        _settings = _settings with { Key = "new-key" };
        monitor.OnSettingsChanged();

        gate.SetResult();

        Assert.NotEqual(StatusLight.Blue, _hub.Gemini.Light);
        Assert.Empty(_health);
    }

    [Fact]
    public void ADebounceThatEndsWhileAProbeIsInFlight_IsProbedRightAfterIt()
    {
        var gate = new TaskCompletionSource();
        var probe = new FakeProbe(_ => Online(), _clock) { Gates = { gate } };
        var monitor = Monitor(probe, Pipeline());
        _clock.Advance(8 * Second);
        _settings = _settings with { Key = "new-key" };
        monitor.OnSettingsChanged();
        _clock.Advance(3 * Second); // the debounce ends while the first probe still waits
        Assert.Single(probe.Calls);

        gate.SetResult(); // the old result is dropped, and the probe the change asked for runs now
        _clock.Advance(Second);

        Assert.Equal(2, probe.Calls.Count);
        Assert.Equal(StatusLight.Green, _hub.Gemini.Light);
    }

    [Fact]
    public void AfterDispose_NothingRuns()
    {
        var probe = new FakeProbe(_ => Online(), _clock);
        var monitor = Monitor(probe, Pipeline());

        monitor.Dispose();
        _clock.Advance(TimeSpan.FromHours(1));

        Assert.Empty(probe.Calls);
    }

    // ---- through the real probe and a fake HTTP handler ----

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return respond(ct);
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private (Handler Handler, GeminiHealthProbe Probe) RealProbe(Func<CancellationToken, Task<HttpResponseMessage>> respond)
    {
        var handler = new Handler(respond);
        return (handler, new GeminiHealthProbe(new HttpClient(handler), () => _settings.Key, () => _settings.Model, clock: _clock));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "timed out waiting for the probe");
    }

    private const string OkBody = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}";

    [Fact]
    public async Task RealProbe_Success_TurnsTheDotGreen()
    {
        var (handler, probe) = RealProbe(_ => Task.FromResult(Reply(HttpStatusCode.OK, OkBody)));
        Monitor(probe, Pipeline());

        _clock.Advance(8 * Second);
        await WaitForAsync(() => _hub.GeminiState == GeminiState.Online);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(StatusLight.Green, _hub.Gemini.Light);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "HTTP 401")]
    [InlineData(HttpStatusCode.Forbidden, "HTTP 403")]
    [InlineData(HttpStatusCode.TooManyRequests, "HTTP 429")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    public async Task RealProbe_HttpErrors_TurnTheDotBlueWithTheStatus(HttpStatusCode status, string reason)
    {
        var (_, probe) = RealProbe(_ => Task.FromResult(Reply(status, string.Empty)));
        Monitor(probe, Pipeline());

        _clock.Advance(8 * Second);
        await WaitForAsync(() => _hub.GeminiState == GeminiState.Offline);

        Assert.Equal(StatusLight.Blue, _hub.Gemini.Light);
        Assert.Contains($"Offline: {reason}", _hub.Gemini.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealProbe_InvalidJson_TurnsTheDotBlue()
    {
        var (_, probe) = RealProbe(_ => Task.FromResult(Reply(HttpStatusCode.OK, "{broken")));
        Monitor(probe, Pipeline());

        _clock.Advance(8 * Second);
        await WaitForAsync(() => _hub.GeminiState == GeminiState.Offline);

        Assert.Contains("JSON inv\U000000E1lido", _hub.Gemini.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealProbe_Timeout_TurnsTheDotBlueWithTempoEsgotado()
    {
        var (handler, probe) = RealProbe(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Reply(HttpStatusCode.OK, OkBody);
        });
        Monitor(probe, Pipeline());

        _clock.Advance(8 * Second);
        await WaitForAsync(() => handler.Calls == 1);
        _clock.Advance(GeminiHealthProbe.ProbeTimeout);
        await WaitForAsync(() => _hub.GeminiState == GeminiState.Offline);

        Assert.Contains("Offline: tempo esgotado", _hub.Gemini.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RealProbe_NoKeyOrCorrectionOff_SendsNoHttpRequestAtAll()
    {
        var (handler, probe) = RealProbe(_ => Task.FromResult(Reply(HttpStatusCode.OK, OkBody)));
        _settings = _settings with { Key = null };
        var monitor = Monitor(probe, Pipeline());
        _clock.Advance(TimeSpan.FromHours(1));

        _settings = new GeminiProbeSettings(false, true, Key, "gemini-test");
        monitor.OnSettingsChanged();
        _clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(0, handler.Calls);
    }
}

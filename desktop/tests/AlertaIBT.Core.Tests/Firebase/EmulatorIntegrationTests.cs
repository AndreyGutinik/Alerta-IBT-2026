using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Firebase;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Tests.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.Core.Tests.Firebase;

/// <summary>
/// The site connector against the real Auth and Realtime Database emulators, with the real <c>database.rules.json</c>. Skipped unless
/// the environment variable <c>ALERTAIBT_RTDB_EMULATOR</c> is set, which is how <c>firebase emulators:exec</c> is told to run it:
/// <code>
/// $env:ALERTAIBT_RTDB_EMULATOR = "1"
/// npx firebase-tools@14.27.0 emulators:exec --only auth,database --project demo-alertaibt "dotnet test --project desktop/tests/AlertaIBT.Core.Tests --filter-class AlertaIBT.Core.Tests.Firebase.EmulatorIntegrationTests"
/// </code>
/// It never talks to a real Google service: the project is <c>demo-alertaibt</c> and every address is on this computer.
/// </summary>
public sealed class EmulatorIntegrationTests
{
    private const string Project = "demo-alertaibt";
    private const string DbHost = "http://127.0.0.1:9000";
    private const string Namespace = Project + "-default-rtdb";
    private const string AuthHost = "http://127.0.0.1:9099";

    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static bool EmulatorOn => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ALERTAIBT_RTDB_EMULATOR"));

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // ---- the emulators, directly (the owner of the database: no rules) ----

    private async Task<JsonNode?> AdminAsync(string method, string path, JsonNode? body = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"{DbHost}/{path}.json?ns={Namespace}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "owner");
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{method} {path} -> {(int)response.StatusCode} {text}");
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private async Task<string> CreateAccountAsync(string email, string password)
    {
        var body = new JsonObject { ["email"] = email, ["password"] = password, ["returnSecureToken"] = true };
        using var response = await _http.PostAsync(
            $"{AuthHost}/identitytoolkit.googleapis.com/v1/accounts:signUp?key=fake-key",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonNode.Parse(text)!["localId"]!.GetValue<string>();
    }

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    private static long Ms(TimeSpan ago) => DateTimeOffset.UtcNow.Add(-ago).ToUnixTimeMilliseconds();

    private async Task<JsonNode?> StatusAsync(string id) => await AdminAsync("GET", $"requests/{id}/status");

    private async Task WaitStateAsync(string id, string state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = (await StatusAsync(id))?["state"]?.GetValue<string>();
            if (last == state)
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        Assert.Fail($"request {id} did not reach {state} (is {last ?? "none"})");
    }

    private sealed class Pc : IAsyncDisposable
    {
        private readonly ServiceHarnessLite _service;

        public Pc(string email, string password, string apiKey = "fake-key")
        {
            _service = new ServiceHarnessLite();
            Engine = new SiteEngine(
                new SiteConfig($"{DbHost}?ns={Namespace}", apiKey, email, password),
                new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan },
                _service.Alerts,
                _service.Hub,
                () => new AppSettings(),
                "8.0.0-emulator",
                NullLogger.Instance,
                ownsHttp: true,
                syncDebounce: TimeSpan.FromMilliseconds(50));
        }

        public SiteEngine Engine { get; }

        public FakeDisplay Display => _service.Display;

        public AlertQueue Queue => _service.Queue;

        public async ValueTask DisposeAsync()
        {
            await Engine.StopAsync();
            Engine.Dispose();
            _service.Dispose();
        }
    }

    private sealed class ServiceHarnessLite : IDisposable
    {
        public ServiceHarnessLite()
        {
            var gemini = new FakeCorrector((_, _) => Task.FromResult<ParsedAlert?>(null));
            var offline = new FakeCorrector((raw, _) => Task.FromResult<ParsedAlert?>(AlertParser.Parse(raw)));
            var pipeline = new CorrectionPipeline(gemini, offline, () => FixedRules.Empty, TimeSpan.FromSeconds(3), TimeProvider.System);
            Queue = new AlertQueue(TimeProvider.System);
            Alerts = new AlertService(Queue, pipeline, Display, Notifier, () => new AppSettings(), Hub, NullLogger.Instance);
            Alerts.Start();
        }

        public AlertQueue Queue { get; }

        public FakeDisplay Display { get; } = new();

        public FakeNotifier Notifier { get; } = new();

        public StatusHub Hub { get; } = new();

        public AlertService Alerts { get; }

        public void Dispose() => Alerts.Dispose();
    }

    private async Task<(string Email, string Password, string Uid)> RegisterPcAsync()
    {
        var email = Unique("pc") + "@alertaibt.test";
        const string password = "pc-password-123";
        var uid = await CreateAccountAsync(email, password);
        await AdminAsync("PUT", "devices/" + uid, JsonValue.Create(true));
        return (email, password, uid);
    }

    private async Task<string> MakeUserAsync(string role, bool blocked = false)
    {
        var uid = Unique(role);
        await AdminAsync("PUT", "users/" + uid, new JsonObject
        {
            ["name"] = "Test " + role,
            ["email"] = uid + "@example.com",
            ["role"] = role,
            ["blocked"] = blocked,
            ["createdAt"] = Ms(TimeSpan.FromDays(1)),
        });
        return uid;
    }

    private async Task SendAsync(string id, string uid, string raw, TimeSpan? age = null, bool urgent = false)
    {
        await AdminAsync("PUT", "requests/" + id, new JsonObject
        {
            ["uid"] = uid,
            ["name"] = "Test",
            ["raw"] = raw,
            ["urgent"] = urgent,
            ["createdAt"] = Ms(age ?? TimeSpan.Zero),
            ["cancel"] = false,
            ["preview"] = new JsonObject { ["text"] = "FORGED | PLACA: ZZZ-9Z99", ["kind"] = "vehicle", ["plate"] = "ZZZ-9Z99", ["by"] = "local" },
        });
    }

    [Fact]
    public async Task Requests_flow_through_the_real_rules_from_pending_to_shown_and_live_is_published()
    {
        Assert.SkipUnless(EmulatorOn, "set ALERTAIBT_RTDB_EMULATOR and run under firebase emulators:exec");
        var (email, password, _) = await RegisterPcAsync();
        var member = await MakeUserAsync("member");
        var blocked = await MakeUserAsync("member", blocked: true);
        var old = Unique("old");
        await SendAsync(old, member, "corolla prata abc1d23", age: TimeSpan.FromHours(30));
        await AdminAsync("PUT", $"requests/{old}/status", new JsonObject { ["state"] = "shown", ["at"] = 1 });
        await using var pc = new Pc(email, password);

        pc.Engine.Start();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (pc.Engine.State != SiteState.Online && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        Assert.Equal(SiteState.Online, pc.Engine.State);

        // A good request: showing, then shown; the text comes from the raw text and not from the forged preview.
        var ok = Unique("rq");
        await SendAsync(ok, member, "corolla prata abc1d23 vidro aberto");
        var pass = await pc.Display.NextPass(1);
        await WaitStateAsync(ok, "showing");
        Assert.Contains("ABC-1D23", (await StatusAsync(ok))!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("ZZZ", pass.Text, StringComparison.Ordinal);
        pass.Complete(true);
        await WaitStateAsync(ok, "shown");

        // Refusals, each with the reason the site shows.
        var missing = Unique("rq");
        await SendAsync(missing, member, "corolla prata");
        await WaitStateAsync(missing, "rejected");
        Assert.Equal("faltou placa", (await StatusAsync(missing))!["reason"]!.GetValue<string>());
        var refused = Unique("rq");
        await SendAsync(refused, blocked, "hilux preta xyz9k88");
        await WaitStateAsync(refused, "rejected");
        Assert.Equal("acesso removido", (await StatusAsync(refused))!["reason"]!.GetValue<string>());
        var duplicate = Unique("rq");
        await SendAsync(duplicate, member, "corolla prata abc1d23");
        await WaitStateAsync(duplicate, "rejected");
        Assert.Equal("repetido", (await StatusAsync(duplicate))!["reason"]!.GetValue<string>());

        // A queued request that the sender cancels.
        pc.Display.SetLed(false);
        var queued = Unique("rq");
        await SendAsync(queued, member, "hilux preta xyz9k88");
        await WaitStateAsync(queued, "queued");
        Assert.Equal(1, (await StatusAsync(queued))!["position"]!.GetValue<int>());
        await AdminAsync("PUT", $"requests/{queued}/cancel", JsonValue.Create(true));
        await WaitStateAsync(queued, "cancelled");

        // A request that waited too long is expired, not shown.
        var stale = Unique("rq");
        await SendAsync(stale, member, "civic branco ijk1l22", age: TimeSpan.FromMinutes(40));
        await WaitStateAsync(stale, "expired");

        // The old request was deleted by the PC, which the rules allow only for its account.
        Assert.Null(await AdminAsync("GET", "requests/" + old));

        // /live passed the rules and the server stamped the heartbeat.
        var live = (JsonObject)(await AdminAsync("GET", "live"))!;
        Assert.True(live["heartbeatAt"]!.GetValue<long>() > Ms(TimeSpan.FromMinutes(5)));
        Assert.False(live["led"]!.GetValue<bool>());
        Assert.Equal("8.0.0-emulator", live["version"]!.GetValue<string>());
        Assert.Equal("shown", live["recent"]!.AsArray().First(r => r!["id"]!.GetValue<string>() == ok)!["state"]!.GetValue<string>());
        Assert.Equal(56, live["bar"]!["height"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_second_connection_after_the_token_is_refused_by_the_rules_is_an_auth_error()
    {
        Assert.SkipUnless(EmulatorOn, "set ALERTAIBT_RTDB_EMULATOR and run under firebase emulators:exec");

        // An account that exists but is not in /devices: the rules do not let it read /requests.
        var email = Unique("stranger") + "@alertaibt.test";
        await CreateAccountAsync(email, "stranger-password-1");
        await using var pc = new Pc(email, "stranger-password-1");

        pc.Engine.Start();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (pc.Engine.State != SiteState.AuthError && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        Assert.Equal(SiteState.AuthError, pc.Engine.State);
    }

    [Fact]
    public async Task A_wrong_password_is_an_auth_error_against_the_real_auth_emulator()
    {
        Assert.SkipUnless(EmulatorOn, "set ALERTAIBT_RTDB_EMULATOR and run under firebase emulators:exec");
        var (email, _, _) = await RegisterPcAsync();
        await using var pc = new Pc(email, "not-the-password");

        pc.Engine.Start();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (pc.Engine.State != SiteState.AuthError && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        Assert.Equal(SiteState.AuthError, pc.Engine.State);
    }

    [Fact]
    public async Task The_streaming_client_gets_live_changes_from_the_real_database()
    {
        Assert.SkipUnless(EmulatorOn, "set ALERTAIBT_RTDB_EMULATOR and run under firebase emulators:exec");
        var (email, password, _) = await RegisterPcAsync();
        var auth = new FirebaseAuthClient(_http, "fake-key", FirebaseAuthEndpoints.Emulator());
        await auth.SignInAsync(email, password, Ct);
        var db = new RealtimeDbClient(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan }, $"{DbHost}?ns={Namespace}", auth);
        var listener = new RtdbListener(db, "requests");
        var changes = new List<RtdbChange>();
        listener.Changed += change =>
        {
            lock (changes)
            {
                changes.Add(change);
            }
        };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = Task.Run(() => listener.RunAsync(stop.Token), Ct);
        var id = Unique("rq");

        await AdminAsync("PUT", $"requests/{id}", new JsonObject { ["uid"] = "u", ["raw"] = "x" });
        await AdminAsync("PUT", $"requests/{id}/cancel", JsonValue.Create(true));
        await AdminAsync("PATCH", $"requests/{id}", new JsonObject { ["urgent"] = true });
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !(listener.GetChild(id)?["urgent"] is not null && listener.GetChild(id)?["cancel"] is not null))
        {
            await Task.Delay(50, Ct);
        }

        var child = listener.GetChild(id)!;
        Assert.True(child["cancel"]!.GetValue<bool>());
        Assert.True(child["urgent"]!.GetValue<bool>());
        Assert.Equal("x", child["raw"]!.GetValue<string>());
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        _ = CultureInfo.InvariantCulture;
    }
}

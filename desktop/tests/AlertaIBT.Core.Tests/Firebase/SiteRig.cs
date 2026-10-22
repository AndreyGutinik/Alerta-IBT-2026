using System.Net;
using System.Text.Json.Nodes;
using AlertaIBT.Core.Firebase;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Tests.Services;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Firebase;

/// <summary>A real alert service, a fake Firebase and the site engine between them, on one fake clock.</summary>
internal sealed class SiteRig : IAsyncDisposable
{
    public const string Member = "u-member";
    public const string Admin = "u-admin";
    public const string Blocked = "u-blocked";
    public const string Good = "corolla prata abc1d23 vidro aberto";

    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private readonly object _gate = new();
    private readonly List<(SiteState State, string? Detail)> _states = [];

    public SiteRig(bool ledOn = true, string? password = null, TimeSpan? debounce = null)
    {
        H = new ServiceHarness(start: false);
        if (!ledOn)
        {
            H.Display.SetLed(false);
        }

        H.Service.Start();
        Fake = new FakeFirebase(H.Clock);
        Fake.Set("users/" + Member, User("Ana", "member", false));
        Fake.Set("users/" + Admin, User("Pastor", "admin", false));
        Fake.Set("users/" + Blocked, User("Bia", "member", true));
        Engine = new SiteEngine(
            new SiteConfig("https://x-default-rtdb.firebaseio.com", FakeFirebase.ApiKey, FakeFirebase.Email, password ?? FakeFirebase.Password),
            new HttpClient(Fake),
            H.Service,
            H.Hub,
            () => H.Settings,
            "8.0.0-test",
            Log,
            H.Clock,
            syncDebounce: debounce ?? TimeSpan.Zero);
        Engine.StateChanged += (state, detail) =>
        {
            lock (_gate)
            {
                _states.Add((state, detail));
            }
        };
    }

    public ServiceHarness H { get; }

    public FakeFirebase Fake { get; }

    public SiteEngine Engine { get; }

    public CapturingLogger Log { get; } = new();

    public FakeTimeProvider Clock => H.Clock;

    public SiteState[] States
    {
        get
        {
            lock (_gate)
            {
                return _states.Select(s => s.State).ToArray();
            }
        }
    }

    public string? LastDetail
    {
        get
        {
            lock (_gate)
            {
                return _states.Count == 0 ? null : _states[^1].Detail;
            }
        }
    }

    public static JsonObject User(string name, string role, bool blocked) => new()
    {
        ["name"] = name,
        ["email"] = name.ToLowerInvariant() + "@x.com",
        ["role"] = role,
        ["blocked"] = blocked,
        ["createdAt"] = 1,
    };

    public async Task StartOnlineAsync()
    {
        Engine.Start();
        await Wait.Until(() => Engine.State == SiteState.Online, "site online");
    }

    public long NowMs => Clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>Writes a request the way the site does.</summary>
    public void Send(string id, string raw, string uid = Member, bool urgent = false, TimeSpan? age = null, Action<JsonObject>? tweak = null)
    {
        var request = new JsonObject
        {
            ["uid"] = uid,
            ["name"] = "Someone",
            ["raw"] = raw,
            ["urgent"] = urgent,
            ["createdAt"] = Clock.GetUtcNow().Add(-(age ?? TimeSpan.Zero)).ToUnixTimeMilliseconds(),
            ["cancel"] = false,
        };
        tweak?.Invoke(request);
        Fake.Set("requests/" + id, request);
    }

    public JsonObject? StatusOf(string id) => Fake.Get($"requests/{id}/status") as JsonObject;

    public string? StateOf(string id) => StatusOf(id)?["state"]?.GetValue<string>();

    public string? ReasonOf(string id) => StatusOf(id)?["reason"]?.GetValue<string>();

    public Task WaitState(string id, string state) =>
        WaitFor(() => StateOf(id) == state, $"request {id} to be {state} (is {StateOf(id) ?? "none"})");

    /// <summary>
    /// Waits for a condition. When it takes more than two seconds it also lets the fake clock run, a second at a time: on a loaded
    /// machine the engine may reach a timer after the test stopped moving the clock, and would otherwise wait for ever.
    /// </summary>
    public async Task WaitFor(Func<bool> condition, string what)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            var waited = DateTime.UtcNow - start;
            if (waited > Wait.Limit)
            {
                Assert.Fail(what + " was not met in time");
            }

            await Task.Delay(10, Ct);
            if (waited > TimeSpan.FromSeconds(2))
            {
                Clock.Advance(TimeSpan.FromSeconds(1));
                if (Fake.Streams.Length > 0)
                {
                    Fake.LastStream.KeepAlive();
                }
            }
        }
    }

    public JsonObject? Live => Fake.Get("live") as JsonObject;

    /// <summary>Moves the fake clock in steps, letting the engine run between them (timers made later still fire).</summary>
    public async Task AdvanceAsync(TimeSpan total, TimeSpan? step = null)
    {
        var each = step ?? TimeSpan.FromSeconds(1);
        await Task.Delay(40, Ct);
        var sinceKeepAlive = TimeSpan.Zero;
        for (var left = total; left > TimeSpan.Zero; left -= each)
        {
            var move = left < each ? left : each;
            Clock.Advance(move);

            // The real server sends a keep-alive about every 30 seconds; without them the PC rightly drops the silent connection.
            sinceKeepAlive += move;
            if (sinceKeepAlive >= TimeSpan.FromSeconds(20) && Fake.Streams.Length > 0)
            {
                Fake.LastStream.KeepAlive();
                sinceKeepAlive = TimeSpan.Zero;
            }

            await Task.Delay(3, Ct);
        }

        await Task.Delay(40, Ct);
    }

    public void DenyWrites(Func<string, HttpStatusCode?> rule) =>
        Fake.DbRule = (method, path) => method is "PUT" or "PATCH" or "DELETE" ? rule(path) : null;

    public async ValueTask DisposeAsync()
    {
        await Engine.StopAsync();
        Engine.Dispose();
        H.Dispose();
    }
}

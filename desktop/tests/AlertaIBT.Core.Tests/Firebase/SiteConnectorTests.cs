using AlertaIBT.Core.Firebase;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Tests.Services;
using AlertaIBT.Core.Tests.Settings;

namespace AlertaIBT.Core.Tests.Firebase;

public sealed class SiteConnectorTests : IDisposable
{
    private readonly FakeProtector _protector = new();
    private readonly StatusHub _hub = new();
    private readonly CapturingLogger _log = new();
    private readonly List<FakeEngine> _engines = [];
    private readonly SiteConnector _connector;

    public SiteConnectorTests()
    {
        _connector = new SiteConnector(
            config =>
            {
                var engine = new FakeEngine(config);
                lock (_engines)
                {
                    _engines.Add(engine);
                }

                return engine;
            },
            _hub,
            _protector,
            _log);
    }

    private FakeEngine[] Engines
    {
        get
        {
            lock (_engines)
            {
                return _engines.ToArray();
            }
        }
    }

    private AppSettings Settings(bool enabled = true, string password = "pw-1", string email = "pc@x.com", string key = "key-1", string url = "https://x-default-rtdb.firebaseio.com") =>
        new AppSettings
        {
            Site = new SiteSettings { Enabled = enabled, DatabaseUrl = url, ApiKey = key, DeviceEmail = email },
        }.WithSitePassword(password, _protector);

    public void Dispose() => _connector.Dispose();

    [Fact]
    public async Task Nothing_starts_while_the_site_is_off_or_not_filled_in()
    {
        _connector.Apply(Settings(enabled: false));
        _connector.Apply(new AppSettings { Site = new SiteSettings { Enabled = true, DatabaseUrl = "https://x.firebaseio.com" } });
        await _connector.SettledAsync();

        Assert.Empty(Engines);
        Assert.Equal(SiteState.NotConfigured, _hub.SiteState);
        Assert.Equal(StatusLight.Gray, _hub.Site.Light);
    }

    [Fact]
    public async Task Switching_it_on_starts_one_engine_and_the_hub_follows_its_state()
    {
        _connector.Apply(Settings());
        await _connector.SettledAsync();

        var engine = Assert.Single(Engines);
        Assert.True(engine.Started);
        Assert.Equal(SiteState.Connecting, _hub.SiteState);
        Assert.Equal(StatusLight.Yellow, _hub.Site.Light);
        engine.Raise(SiteState.Online, null);
        Assert.Equal(StatusLight.Green, _hub.Site.Light);
        engine.Raise(SiteState.AuthError, "login recusado");
        Assert.Equal(StatusLight.Red, _hub.Site.Light);
        Assert.Contains("login recusado", _hub.Site.Detail, StringComparison.Ordinal);
        engine.Raise(SiteState.Reconnecting, null);
        Assert.Equal(StatusLight.Yellow, _hub.Site.Light);
        engine.Raise(SiteState.Offline, null);
        Assert.Equal(StatusLight.Red, _hub.Site.Light);
    }

    [Fact]
    public async Task The_engine_gets_the_clear_password()
    {
        _connector.Apply(Settings(password: "p4ssw0rd!"));
        await _connector.SettledAsync();

        var config = Engines[0].Config;
        Assert.Equal("p4ssw0rd!", config.Password);
        Assert.Equal("pc@x.com", config.Email);
        Assert.Equal("key-1", config.ApiKey);
        Assert.Equal("https://x-default-rtdb.firebaseio.com", config.DatabaseUrl);
    }

    [Fact]
    public async Task The_same_settings_do_not_restart_the_engine()
    {
        _connector.Apply(Settings());
        await _connector.SettledAsync();
        _connector.Apply(Settings());
        _connector.Apply(Settings() with { System = new SystemSettings { Hotkey = "Ctrl+Alt+K" } });
        await _connector.SettledAsync();

        Assert.Single(Engines);
        Assert.False(Engines[0].Stopped);
    }

    [Fact]
    public async Task Settings_that_leave_the_connection_alone_tell_the_running_engine_so_the_site_gets_the_new_bar()
    {
        _connector.Apply(Settings());
        await _connector.SettledAsync();
        var before = Engines[0].SettingsChangedCalls;

        _connector.Apply(Settings() with { Bar = new BarSettings { Speed = 200 } });
        await _connector.SettledAsync();

        Assert.Single(Engines);
        Assert.True(Engines[0].SettingsChangedCalls > before);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("email")]
    [InlineData("key")]
    [InlineData("url")]
    public async Task A_changed_credential_stops_the_old_engine_and_starts_a_new_one(string what)
    {
        _connector.Apply(Settings());
        await _connector.SettledAsync();

        _connector.Apply(what switch
        {
            "password" => Settings(password: "pw-2"),
            "email" => Settings(email: "other@x.com"),
            "key" => Settings(key: "key-2"),
            _ => Settings(url: "https://other-default-rtdb.firebaseio.com"),
        });
        await _connector.SettledAsync();

        Assert.Equal(2, Engines.Length);
        Assert.True(Engines[0].Stopped);
        Assert.True(Engines[1].Started);
        Assert.False(Engines[1].Stopped);
    }

    [Fact]
    public async Task Switching_it_off_stops_the_engine_and_the_dot_goes_gray()
    {
        _connector.Apply(Settings());
        await _connector.SettledAsync();
        Engines[0].Raise(SiteState.Online, null);

        _connector.Apply(Settings(enabled: false));
        await _connector.SettledAsync();

        Assert.True(Engines[0].Stopped);
        Assert.Equal(SiteState.NotConfigured, _hub.SiteState);
    }

    [Fact]
    public async Task A_late_event_of_a_stopped_engine_does_not_change_the_dot()
    {
        _connector.Apply(Settings());
        await _connector.SettledAsync();
        var old = Engines[0];
        _connector.Apply(Settings(enabled: false));
        await _connector.SettledAsync();

        old.Raise(SiteState.Online, null);

        Assert.Equal(SiteState.NotConfigured, _hub.SiteState);
    }

    [Fact]
    public async Task Many_quick_changes_end_with_the_last_one_running()
    {
        for (var i = 0; i < 20; i++)
        {
            _connector.Apply(Settings(password: "pw-" + i));
        }

        await _connector.SettledAsync();

        Assert.Equal("pw-19", Engines[^1].Config.Password);
        Assert.Equal(1, Engines.Count(e => !e.Stopped));
    }

    [Fact]
    public async Task An_engine_that_cannot_be_made_is_logged_and_shown_without_throwing()
    {
        var connector = new SiteConnector(_ => throw new InvalidOperationException("boom"), _hub, _protector, _log);

        connector.Apply(Settings());
        await connector.SettledAsync();

        Assert.Equal(SiteState.Offline, _hub.SiteState);
        Assert.NotNull(_hub.LastError);
        connector.Dispose();
    }

    [Fact]
    public async Task A_password_that_cannot_be_read_counts_as_not_configured()
    {
        var settings = Settings() with { Site = Settings().Site with { PasswordProtected = "%%% not base64 %%%" } };

        _connector.Apply(settings);
        await _connector.SettledAsync();

        Assert.Empty(Engines);
        Assert.Equal(SiteState.NotConfigured, _hub.SiteState);
    }

    [Fact]
    public async Task Stopping_stops_the_engine_and_later_changes_do_nothing()
    {
        _connector.Apply(Settings());
        await _connector.SettledAsync();

        await _connector.StopAsync();
        _connector.Apply(Settings(password: "pw-2"));
        await _connector.SettledAsync();

        Assert.Single(Engines);
        Assert.True(Engines[0].Stopped);
    }

    private sealed class FakeEngine(SiteConfig config) : ISiteEngine, IDisposable
    {
        public SiteConfig Config { get; } = config;

        public bool Started { get; private set; }

        public bool Stopped { get; private set; }

        public event Action<SiteState, string?>? StateChanged;

        public int SettingsChangedCalls => Volatile.Read(ref _settingsChangedCalls);

        private int _settingsChangedCalls;

        public void Start() => Started = true;

        public void SettingsChanged() => Interlocked.Increment(ref _settingsChangedCalls);


        public Task StopAsync()
        {
            Stopped = true;
            return Task.CompletedTask;
        }

        public void Raise(SiteState state, string? detail) => StateChanged?.Invoke(state, detail);

        public void Dispose()
        {
        }
    }
}

using AlertaIBT.Core.Firebase;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Tests.Settings;

public class SiteSettingsTests
{
    private static readonly FakeProtector Protector = new();

    [Fact]
    public void The_site_is_off_and_empty_by_default()
    {
        var site = new AppSettings().Site;

        Assert.False(site.Enabled);
        Assert.Equal(string.Empty, site.DatabaseUrl);
        Assert.Equal(string.Empty, site.ApiKey);
        Assert.Equal(string.Empty, site.ProjectId);
        Assert.Equal(string.Empty, site.DeviceEmail);
        Assert.Null(site.PasswordProtected);
        Assert.False(site.IsConfigured);
    }

    [Fact]
    public void A_settings_file_from_before_the_site_existed_loads_with_the_defaults()
    {
        const string old = "{\"version\":1,\"bar\":{\"height\":80},\"telegram\":{\"chatId\":-100123},\"system\":{\"hotkey\":\"Ctrl+Alt+K\"}}";

        var (settings, corrupt) = SettingsJson.Load(old);

        Assert.False(corrupt);
        Assert.Equal(new SiteSettings(), settings.Site);
        Assert.Equal(80, settings.Bar.Height);
        Assert.Equal(-100123, settings.Telegram.ChatId);
    }

    [Fact]
    public void The_site_settings_survive_a_save_and_a_load()
    {
        var settings = new AppSettings
        {
            Site = new SiteSettings
            {
                Enabled = true,
                DatabaseUrl = "https://alertaibt-default-rtdb.firebaseio.com",
                ApiKey = "AIzaSyTest",
                ProjectId = "alertaibt",
                DeviceEmail = "pc-telao@alertaibt.web.app",
            },
        }.WithSitePassword("p4ss-w0rd-xyz", Protector);

        var (loaded, corrupt) = SettingsJson.Load(SettingsJson.Save(settings));

        Assert.False(corrupt);
        Assert.Equal(settings.Site, loaded.Site);
        Assert.Equal("p4ss-w0rd-xyz", loaded.GetSitePassword(Protector));
        Assert.True(loaded.Site.IsConfigured);
    }

    [Fact]
    public void The_password_is_never_in_the_file_in_clear()
    {
        var settings = new AppSettings
        {
            Site = new SiteSettings { DatabaseUrl = "https://x-default-rtdb.firebaseio.com", ApiKey = "k", DeviceEmail = "pc@x.com" },
        }.WithSitePassword("TopSecret-123", Protector);

        var json = SettingsJson.Save(settings);

        Assert.DoesNotContain("TopSecret", json, StringComparison.Ordinal);
        Assert.Contains("passwordProtected", json, StringComparison.Ordinal);
        Assert.DoesNotContain("TopSecret", settings.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_password_is_kept_as_typed_and_an_empty_one_clears_it()
    {
        var settings = new AppSettings().WithSitePassword(" spaced pass ", Protector);

        Assert.Equal(" spaced pass ", settings.GetSitePassword(Protector));
        Assert.Null(settings.WithSitePassword(string.Empty, Protector).Site.PasswordProtected);
        Assert.Null(settings.WithSitePassword(null, Protector).GetSitePassword(Protector));
    }

    [Fact]
    public void Setting_the_password_changes_nothing_else()
    {
        var before = new AppSettings { Site = new SiteSettings { Enabled = true, ApiKey = "k" } };

        var after = before.WithSitePassword("x", Protector);

        Assert.Equal(before with { Site = after.Site }, after);
        Assert.True(after.Site.Enabled);
        Assert.Equal("k", after.Site.ApiKey);
    }

    [Theory]
    [InlineData("https://alertaibt-default-rtdb.firebaseio.com", true)]
    [InlineData("https://alertaibt-default-rtdb.us-central1.firebasedatabase.app", true)]
    [InlineData("https://alertaibt-default-rtdb.europe-west1.firebasedatabase.app/", true)]
    [InlineData("HTTPS://X-DEFAULT-RTDB.FIREBASEIO.COM", true)]
    [InlineData("http://127.0.0.1:9000?ns=demo-alertaibt", true)]
    [InlineData("http://localhost:9000", true)]
    [InlineData("https://127.0.0.1:9000", true)]
    [InlineData("http://alertaibt-default-rtdb.firebaseio.com", false)]
    [InlineData("https://evil.example.com", false)]
    [InlineData("https://firebaseio.com.evil.example.com", false)]
    [InlineData("https://evilfirebaseio.com", false)]
    [InlineData("https://x.firebaseio.com@evil.example.com", false)]
    [InlineData("https://user:pw@x-default-rtdb.firebaseio.com", false)]
    [InlineData("ftp://x.firebaseio.com", false)]
    [InlineData("alertaibt-default-rtdb.firebaseio.com", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void Only_a_firebase_database_or_a_local_emulator_is_a_valid_address(string? url, bool valid)
    {
        Assert.Equal(valid, SiteSettings.IsValidDatabaseUrl(url));
    }

    [Fact]
    public void An_invalid_address_is_dropped_when_the_settings_are_held_in_range()
    {
        var settings = new AppSettings { Site = new SiteSettings { DatabaseUrl = "https://evil.example.com", ApiKey = "  key  ", DeviceEmail = " pc@x.com " } };

        var held = SettingsJson.Normalize(settings);

        Assert.Equal(string.Empty, held.Site.DatabaseUrl);
        Assert.Equal("key", held.Site.ApiKey);
        Assert.Equal("pc@x.com", held.Site.DeviceEmail);
        Assert.Equal(held, SettingsJson.Normalize(held));
    }

    [Fact]
    public void A_valid_address_is_trimmed_and_loses_its_trailing_slash()
    {
        var held = SettingsJson.Normalize(new AppSettings { Site = new SiteSettings { DatabaseUrl = "  https://x-default-rtdb.firebaseio.com/  " } });

        Assert.Equal("https://x-default-rtdb.firebaseio.com", held.Site.DatabaseUrl);
    }

    [Fact]
    public void Wrong_types_in_the_site_section_give_the_defaults()
    {
        var (settings, corrupt) = SettingsJson.Load("{\"site\":{\"enabled\":\"yes\",\"databaseUrl\":5,\"apiKey\":[],\"passwordProtected\":false}}");

        Assert.False(corrupt);
        Assert.Equal(new SiteSettings(), settings.Site);
    }

    [Fact]
    public void The_site_is_configured_only_with_everything_filled_in()
    {
        var full = new SiteSettings
        {
            DatabaseUrl = "https://x-default-rtdb.firebaseio.com",
            ApiKey = "k",
            DeviceEmail = "pc@x.com",
            PasswordProtected = "p",
        };

        Assert.True(full.IsConfigured);
        Assert.False((full with { DatabaseUrl = string.Empty }).IsConfigured);
        Assert.False((full with { ApiKey = string.Empty }).IsConfigured);
        Assert.False((full with { DeviceEmail = string.Empty }).IsConfigured);
        Assert.False((full with { PasswordProtected = null }).IsConfigured);
    }

    [Theory]
    [InlineData(SiteState.NotConfigured, StatusLight.Gray)]
    [InlineData(SiteState.Connecting, StatusLight.Yellow)]
    [InlineData(SiteState.Online, StatusLight.Green)]
    [InlineData(SiteState.Reconnecting, StatusLight.Yellow)]
    [InlineData(SiteState.Offline, StatusLight.Red)]
    [InlineData(SiteState.AuthError, StatusLight.Red)]
    public void The_site_dot_has_its_color(SiteState state, StatusLight light)
    {
        var hub = new StatusHub();
        var raised = new List<string?>();
        hub.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        hub.SetSite(state);

        Assert.Equal(light, hub.Site.Light);
        Assert.Equal(state, hub.SiteState);
        if (state != SiteState.NotConfigured)
        {
            Assert.Contains(nameof(StatusHub.Site), raised);
            Assert.Contains(nameof(StatusHub.SiteState), raised);
        }
    }

    [Fact]
    public void The_site_dot_shows_the_detail_of_a_problem_and_a_new_state_replaces_it()
    {
        var hub = new StatusHub();

        hub.SetSite(SiteState.AuthError, "senha errada");
        Assert.Equal("senha errada", hub.Site.Detail);
        Assert.Equal("Erro de login", hub.Site.Text);
        hub.SetSite(SiteState.Online);

        Assert.Equal("Conectado", hub.Site.Text);
        Assert.DoesNotContain("senha", hub.Site.Detail, StringComparison.Ordinal);
    }
}

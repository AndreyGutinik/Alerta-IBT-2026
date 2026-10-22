using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Firebase;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.ViewModels;

// The "Site" block of Configuracoes: address, API key, e-mail, password behind a lock, and the switch; all saved 600 ms later.
public sealed class SettingsViewModelSiteTests : IDisposable
{
    private const string Url = "https://alertaibt-default-rtdb.firebaseio.com";
    private const string Password = "Xk3-pQ9z-Lm7vRt2";

    private readonly SettingsRig _rig = new();
    private readonly SettingsViewModel _vm;

    public SettingsViewModelSiteTests() => _vm = Create(_rig);

    public void Dispose()
    {
        _vm.Dispose();
        _rig.Dispose();
    }

    private SettingsViewModel Create(SettingsRig rig) =>
        new(
            rig.Settings,
            rig.Protector,
            rig.Hub,
            rig.Saver,
            new DiagnosticsViewModel(new LogFiles(Path.Combine(rig.Folder, "logs")), _ => { }, () => null),
            () => Task.CompletedTask);

    [Fact]
    public void The_site_block_starts_empty_and_off_and_loading_saves_nothing()
    {
        Assert.False(_vm.SiteEnabled);
        Assert.Equal(string.Empty, _vm.SiteUrl.Text);
        Assert.Equal(string.Empty, _vm.SiteApiKey.Text);
        Assert.Equal(string.Empty, _vm.SiteEmail.Text);
        Assert.False(_vm.SitePassword.IsLocked);
        Assert.False(_rig.Saver.HasPending);
    }

    [Fact]
    public void The_block_shows_what_the_settings_hold_and_a_stored_password_only_as_dots()
    {
        var stored = new AppSettings
        {
            Site = new SiteSettings { Enabled = true, DatabaseUrl = Url, ApiKey = "AIzaSyTestKey123", DeviceEmail = "pc@alertaibt.web.app" },
        }.WithSitePassword(Password, new ReversingProtector());
        using var rig = new SettingsRig(stored);
        using var vm = Create(rig);

        Assert.True(vm.SiteEnabled);
        Assert.Equal(Url, vm.SiteUrl.Text);
        Assert.Equal("AIzaSyTestKey123", vm.SiteApiKey.Text);
        Assert.Equal("pc@alertaibt.web.app", vm.SiteEmail.Text);
        Assert.True(vm.SitePassword.IsLocked);
        Assert.Equal(string.Empty, vm.SitePassword.Entry);
        Assert.Equal("Trocar", vm.SitePassword.LockButtonText);
        Assert.False(rig.Saver.HasPending);
    }

    [Fact]
    public void Everything_typed_is_saved_after_the_pause_and_the_password_is_never_in_the_file_in_clear()
    {
        _vm.SiteUrl.Text = Url;
        _vm.SiteApiKey.Text = " AIzaSyTestKey123 ";
        _vm.SiteEmail.Text = "pc@alertaibt.web.app";
        _vm.SitePassword.Entry = Password;
        _vm.SiteEnabled = true;
        _rig.Rest();

        var saved = _rig.OnDisk();
        Assert.True(saved.Site.Enabled);
        Assert.Equal(Url, saved.Site.DatabaseUrl);
        Assert.Equal("AIzaSyTestKey123", saved.Site.ApiKey);
        Assert.Equal("pc@alertaibt.web.app", saved.Site.DeviceEmail);
        Assert.Equal(Password, saved.GetSitePassword(_rig.Protector));
        Assert.True(saved.Site.IsConfigured);
        Assert.DoesNotContain(Password, _rig.FileText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Xk3-pQ9z", _rig.FileText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_is_saved_before_the_pause()
    {
        _vm.SiteEmail.Text = "pc@alertaibt.web.app";
        _rig.Clock.Advance(TimeSpan.FromMilliseconds(500));

        Assert.True(_rig.Saver.HasPending);
        Assert.Equal(string.Empty, _rig.OnDisk().Site.DeviceEmail);
        _rig.Clock.Advance(TimeSpan.FromMilliseconds(150));
        Assert.Equal("pc@alertaibt.web.app", _rig.OnDisk().Site.DeviceEmail);
    }

    [Fact]
    public void After_saving_the_password_the_field_goes_back_to_dots()
    {
        _vm.SitePassword.Entry = Password;
        _rig.Rest();

        Assert.True(_vm.SitePassword.IsLocked);
        Assert.Equal(string.Empty, _vm.SitePassword.Entry);
        Assert.Equal("Trocar", _vm.SitePassword.LockButtonText);
    }

    [Fact]
    public void Trocar_opens_the_field_and_a_new_password_replaces_the_old_one()
    {
        using var rig = new SettingsRig(new AppSettings().WithSitePassword(Password, new ReversingProtector()));
        using var vm = Create(rig);
        vm.SitePassword.Entry = "typed-in-a-locked-field";
        rig.Rest();
        Assert.Equal(Password, rig.OnDisk().GetSitePassword(rig.Protector));

        vm.SitePassword.Unlock();
        vm.SitePassword.Entry = "A-brand-new-pass-9";
        rig.Rest();

        Assert.Equal("A-brand-new-pass-9", rig.OnDisk().GetSitePassword(rig.Protector));
        Assert.True(vm.SitePassword.IsLocked);
    }

    [Theory]
    [InlineData("http://alertaibt-default-rtdb.firebaseio.com")]
    [InlineData("https://evil.example.com")]
    [InlineData("alertaibt-default-rtdb.firebaseio.com")]
    [InlineData("https://firebaseio.com.evil.example.com")]
    public void An_address_that_is_not_a_firebase_database_is_refused_with_a_hint_and_never_saved(string typed)
    {
        _vm.SiteUrl.Text = typed;
        _rig.Rest();

        Assert.True(_vm.SiteUrl.HasHint);
        Assert.Contains("firebaseio.com", _vm.SiteUrl.Hint, StringComparison.Ordinal);
        Assert.Equal(string.Empty, _rig.OnDisk().Site.DatabaseUrl);
        Assert.Equal(string.Empty, _vm.SiteUrl.Accepted);
    }

    [Theory]
    [InlineData("https://alertaibt-default-rtdb.us-central1.firebasedatabase.app")]
    [InlineData("http://127.0.0.1:9000?ns=demo-alertaibt-default-rtdb")]
    public void The_other_database_addresses_and_the_local_emulator_are_accepted(string typed)
    {
        _vm.SiteUrl.Text = typed;
        _rig.Rest();

        Assert.False(_vm.SiteUrl.HasHint);
        Assert.Equal(typed, _rig.OnDisk().Site.DatabaseUrl);
    }

    [Fact]
    public void A_bad_value_cancels_a_waiting_save_of_the_one_before_and_leaving_the_box_restores_the_kept_value()
    {
        _vm.SiteUrl.Text = Url;
        _rig.Rest();
        _vm.SiteUrl.Text = Url + "x/../";
        _vm.SiteUrl.Text = "https://nope.example.com";
        _rig.Rest();

        Assert.Equal(Url, _rig.OnDisk().Site.DatabaseUrl);
        Assert.True(_vm.SiteUrl.HasHint);
        _vm.SiteUrl.Normalize();
        Assert.Equal(Url, _vm.SiteUrl.Text);
        Assert.False(_vm.SiteUrl.HasHint);
    }

    [Theory]
    [InlineData("pc")]
    [InlineData("pc@")]
    [InlineData("@x.com")]
    [InlineData("pc @x.com")]
    [InlineData("a@b@c.com")]
    public void A_bad_email_is_refused(string typed)
    {
        _vm.SiteEmail.Text = typed;
        _rig.Rest();

        Assert.True(_vm.SiteEmail.HasHint);
        Assert.Equal(string.Empty, _rig.OnDisk().Site.DeviceEmail);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("12345")]
    public void A_password_under_six_letters_is_refused(string typed)
    {
        _vm.SitePassword.Entry = typed;
        _rig.Rest();

        Assert.True(_vm.SitePassword.HasHint);
        Assert.Null(_rig.OnDisk().Site.PasswordProtected);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has space in it 1234")]
    public void A_bad_api_key_is_refused(string typed)
    {
        _vm.SiteApiKey.Text = typed;
        _rig.Rest();

        Assert.True(_vm.SiteApiKey.HasHint);
        Assert.Equal(string.Empty, _rig.OnDisk().Site.ApiKey);
    }

    [Fact]
    public void Blank_boxes_clear_the_values()
    {
        _vm.SiteEmail.Text = "pc@alertaibt.web.app";
        _rig.Rest();

        _vm.SiteEmail.Text = "   ";
        _rig.Rest();

        Assert.Equal(string.Empty, _rig.OnDisk().Site.DeviceEmail);
        Assert.False(_vm.SiteEmail.HasHint);
    }

    [Fact]
    public void The_switch_saves_only_its_own_value()
    {
        _vm.SiteEmail.Text = "pc@alertaibt.web.app";
        _rig.Rest();
        _rig.Settings.Update(s => s with { Telegram = s.Telegram with { Offset = 77 } });

        _vm.SiteEnabled = true;
        _rig.Rest();

        var saved = _rig.OnDisk();
        Assert.True(saved.Site.Enabled);
        Assert.Equal("pc@alertaibt.web.app", saved.Site.DeviceEmail);
        Assert.Equal(77, saved.Telegram.Offset);
    }

    [Fact]
    public void The_site_dot_of_the_tab_follows_the_connection_state()
    {
        _rig.Hub.SetSite(SiteState.Online);
        Assert.Equal(StatusLight.Green, _vm.SiteLight);
        _rig.Hub.SetSite(SiteState.AuthError, "senha errada");
        Assert.Equal(StatusLight.Red, _vm.SiteLight);
        Assert.Equal("Erro de login", _vm.SiteText);
        _rig.Hub.SetSite(SiteState.Reconnecting);
        Assert.Equal(StatusLight.Yellow, _vm.SiteLight);
    }
}

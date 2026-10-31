using System.Net;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Control;
using AlertaIBT.Core.Holyrics;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.ViewModels;

// The Holyrics card of the Controle tab: the address, the token (protected, never shown again), "Testar" and the chip, the arrows switch.
public sealed class ControlHolyricsViewModelTests
{
    private const string Token = "TOKEN-FOR-TESTS-777";

    private static void StoreToken(ControlRig rig)
    {
        rig.Vm.PasteHolyricsToken(Token);
        rig.Vm.ApplyPendingToken();
    }

    [Fact]
    public void It_starts_with_this_pc_8091_the_arrows_on_and_no_token()
    {
        using var rig = new ControlRig();

        Assert.Equal("127.0.0.1", rig.Vm.HolyricsHostText);
        Assert.Equal("8091", rig.Vm.HolyricsPort.Text);
        Assert.True(rig.Vm.HolyricsArrows);
        Assert.False(rig.Vm.HolyricsToken.HasStored);
        Assert.Equal("sem token", rig.Vm.HolyricsStatusText);
        Assert.Equal("off", rig.Vm.HolyricsTone);
        Assert.False(rig.Vm.TestHolyricsCommand.CanExecute(null));
    }

    [Fact]
    public void A_valid_host_is_saved_and_one_that_cannot_be_an_address_is_flagged_and_not_saved()
    {
        using var rig = new ControlRig();

        rig.Vm.HolyricsHostText = "192.168.0.50";
        Assert.Equal("192.168.0.50", rig.Settings.Holyrics.Host);
        Assert.False(rig.Vm.HolyricsHostInvalid);

        rig.Vm.HolyricsHostText = "my host";
        Assert.True(rig.Vm.HolyricsHostInvalid);
        Assert.Equal("192.168.0.50", rig.Settings.Holyrics.Host);
    }

    [Fact]
    public void The_port_is_saved_and_a_wrong_one_is_not()
    {
        using var rig = new ControlRig();

        rig.Vm.HolyricsPort.Text = "9001";
        Assert.Equal(9001, rig.Settings.Holyrics.Port);

        rig.Vm.HolyricsPort.Text = "80a";
        Assert.Equal(9001, rig.Settings.Holyrics.Port);
        Assert.NotNull(rig.Vm.HolyricsPort.Hint);
    }

    [Fact]
    public void A_pasted_token_is_kept_protected_the_field_goes_back_to_dots_and_nothing_shows_it_again()
    {
        using var rig = new ControlRig();

        rig.Vm.PasteHolyricsToken("  " + Token + "  ");
        Assert.False(rig.Vm.HolyricsToken.HasStored); // it rests a moment before it is kept
        rig.Vm.ApplyPendingToken();

        Assert.True(rig.Vm.HolyricsToken.HasStored);
        Assert.True(rig.Vm.HolyricsToken.IsLocked);
        Assert.Equal(string.Empty, rig.Vm.HolyricsToken.Entry);
        Assert.Equal(Token, rig.Settings.GetHolyricsToken(rig.Chrome.Protector));
        Assert.DoesNotContain(Token, SettingsJson.Save(rig.Settings), StringComparison.Ordinal);
        Assert.Equal("n\U000000E3o testado", rig.Vm.HolyricsStatusText);
        Assert.True(rig.Vm.TestHolyricsCommand.CanExecute(null));
    }

    [Fact]
    public void A_token_with_spaces_inside_is_refused_with_a_hint_and_not_kept()
    {
        using var rig = new ControlRig();

        rig.Vm.PasteHolyricsToken("abc def ghi");
        rig.Vm.ApplyPendingToken();

        Assert.NotNull(rig.Vm.HolyricsToken.Hint);
        Assert.False(rig.Vm.HolyricsToken.HasStored);
    }

    [Fact]
    public void Pasting_when_the_token_is_locked_opens_the_field_first_and_replaces_it()
    {
        using var rig = new ControlRig();
        StoreToken(rig);
        Assert.True(rig.Vm.HolyricsToken.IsLocked);

        rig.Vm.PasteHolyricsToken("ANOTHER-TOKEN-888");
        rig.Vm.ApplyPendingToken();

        Assert.Equal("ANOTHER-TOKEN-888", rig.Settings.GetHolyricsToken(rig.Chrome.Protector));
    }

    [Fact]
    public void A_blank_clipboard_changes_nothing()
    {
        using var rig = new ControlRig();
        StoreToken(rig);

        rig.Vm.PasteHolyricsToken("   ");
        rig.Vm.PasteHolyricsToken(null);

        Assert.True(rig.Vm.HolyricsToken.IsLocked);
        Assert.Equal(Token, rig.Settings.GetHolyricsToken(rig.Chrome.Protector));
    }

    [Fact]
    public void The_arrows_switch_is_saved()
    {
        using var rig = new ControlRig();

        rig.Vm.HolyricsArrows = false;

        Assert.False(rig.Settings.Holyrics.ArrowsEnabled);
        Assert.False(rig.Holyrics.ArrowsActive);
        rig.Vm.HolyricsArrows = true;
        Assert.True(rig.Settings.Holyrics.ArrowsEnabled);
    }

    [Fact]
    public async Task Testar_shows_connected_with_the_state_of_the_video()
    {
        using var rig = new ControlRig();
        StoreToken(rig);
        rig.HolyricsHandler.Reply = action => action == "GetMediaPlayerInfo"
            ? (HttpStatusCode.OK, "{\"status\":\"ok\",\"data\":{\"playing\":false}}")
            : (HttpStatusCode.OK, "{\"status\":\"ok\"}");

        await rig.Vm.TestHolyricsCommand.ExecuteAsync(null);

        Assert.Equal(HolyricsLink.Connected, rig.Vm.HolyricsLink);
        Assert.Equal("conectado", rig.Vm.HolyricsStatusText);
        Assert.Equal("ok", rig.Vm.HolyricsTone);
        Assert.Contains("parado", rig.Vm.HolyricsStatusTip, StringComparison.Ordinal);
        Assert.Equal(["CheckPermissions", "GetMediaPlayerInfo"], rig.HolyricsHandler.Actions);
    }

    [Fact]
    public async Task Testar_names_the_missing_permission()
    {
        using var rig = new ControlRig();
        StoreToken(rig);
        rig.HolyricsHandler.Reply = _ => (HttpStatusCode.Unauthorized, "{\"status\":\"error\",\"error\":{\"unauthorized_actions\":\"SetLogo\"}}");

        await rig.Vm.TestHolyricsCommand.ExecuteAsync(null);

        Assert.Equal("token sem permiss\U000000E3o para SetLogo", rig.Vm.HolyricsStatusText);
        Assert.Equal("warn", rig.Vm.HolyricsTone);
    }

    [Fact]
    public async Task Testar_says_no_answer_when_holyrics_is_off()
    {
        using var rig = new ControlRig();
        StoreToken(rig);
        rig.HolyricsHandler.Down = true;

        await rig.Vm.TestHolyricsCommand.ExecuteAsync(null);

        Assert.StartsWith("sem resposta: ", rig.Vm.HolyricsStatusText, StringComparison.Ordinal);
        Assert.Equal("bad", rig.Vm.HolyricsTone);
    }

    [Fact]
    public async Task A_failed_test_puts_the_reason_in_the_chip_the_full_facts_in_the_tooltip_and_never_the_token()
    {
        using var rig = new ControlRig();
        StoreToken(rig);
        rig.HolyricsHandler.Down = true;

        await rig.Vm.TestHolyricsCommand.ExecuteAsync(null);

        Assert.StartsWith("sem resposta: ", rig.Vm.HolyricsStatusText, StringComparison.Ordinal);
        Assert.Contains("CheckPermissions", rig.Vm.HolyricsStatusTip, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:8091", rig.Vm.HolyricsStatusTip, StringComparison.Ordinal);
        Assert.Contains("tempo: ", rig.Vm.HolyricsStatusTip, StringComparison.Ordinal);
        Assert.True(rig.Vm.HasHolyricsReason);
        Assert.Equal(rig.Vm.HolyricsStatusText, rig.Vm.HolyricsReasonText);
        Assert.DoesNotContain(Token, rig.Vm.HolyricsStatusText + rig.Vm.HolyricsStatusTip + rig.Vm.HolyricsReasonText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pending_permission_shows_amber_and_names_the_actions()
    {
        using var rig = new ControlRig();
        StoreToken(rig);
        rig.HolyricsHandler.Reply = _ => (HttpStatusCode.Unauthorized, "{\"status\":\"error\",\"error\":{\"unauthorized_actions\":\"SetLogo,ActionNext\",\"request_status\":\"pending\"}}");

        await rig.Vm.TestHolyricsCommand.ExecuteAsync(null);

        Assert.Equal("permiss\U000000E3o pendente no Holyrics para SetLogo, ActionNext", rig.Vm.HolyricsStatusText);
        Assert.Equal("warn", rig.Vm.HolyricsTone);
        Assert.True(rig.Vm.HasHolyricsReason);
    }

    [Fact]
    public async Task A_connected_chip_has_no_reason_line()
    {
        using var rig = new ControlRig();
        StoreToken(rig);

        await rig.Vm.TestHolyricsCommand.ExecuteAsync(null);

        Assert.False(rig.Vm.HasHolyricsReason);
        Assert.Equal(string.Empty, rig.Vm.HolyricsReasonText);
    }

    [Fact]
    public void An_address_typed_with_a_scheme_and_a_port_is_cleaned_the_port_moves_and_a_note_says_so()
    {
        using var rig = new ControlRig();

        rig.Vm.HolyricsHostText = "http://192.168.0.50:9001/";

        Assert.False(rig.Vm.HolyricsHostInvalid);
        Assert.Equal("192.168.0.50", rig.Settings.Holyrics.Host);
        Assert.Equal(9001, rig.Settings.Holyrics.Port);
        Assert.Equal("9001", rig.Vm.HolyricsPort.Text);
        Assert.True(rig.Vm.HasHolyricsHostHint);

        rig.Vm.NormalizeHolyricsHostText();

        Assert.Equal("192.168.0.50", rig.Vm.HolyricsHostText);
        Assert.True(rig.Vm.HasHolyricsHostHint); // the note stays until the next edit
    }

    [Fact]
    public void An_address_with_spaces_is_refused_with_the_reason_and_not_saved()
    {
        using var rig = new ControlRig();

        rig.Vm.HolyricsHostText = "127.0.0.1 8091";

        Assert.True(rig.Vm.HolyricsHostInvalid);
        Assert.Contains("espa", rig.Vm.HolyricsHostHint, StringComparison.Ordinal);
        Assert.Equal("127.0.0.1", rig.Settings.Holyrics.Host);
    }

    [Fact]
    public async Task Changing_the_token_clears_the_chip_back_to_not_tested()
    {
        using var rig = new ControlRig();
        StoreToken(rig);
        await rig.Vm.TestHolyricsCommand.ExecuteAsync(null);
        Assert.Equal("conectado", rig.Vm.HolyricsStatusText);

        rig.Vm.PasteHolyricsToken("NEW-TOKEN-FOR-TESTS-999");
        rig.Vm.ApplyPendingToken();

        Assert.Equal("n\U000000E3o testado", rig.Vm.HolyricsStatusText);
    }

    [Fact]
    public void The_holyrics_rows_test_button_works_and_runs_the_action()
    {
        using var rig = new ControlRig();

        rig.Vm.Rows[12].TestCommand.Execute(null); // slide next
        rig.Vm.Rows[15].TestCommand.Execute(null); // logo

        var until = Environment.TickCount64 + 3000;
        while (rig.Targets.Ran.Count < 2 && Environment.TickCount64 < until)
        {
            Thread.Sleep(5);
        }

        Assert.Equal([ControlAction.SlideNext, ControlAction.LogoToggle], rig.Targets.Ran.Select(c => c.Action));
    }
}

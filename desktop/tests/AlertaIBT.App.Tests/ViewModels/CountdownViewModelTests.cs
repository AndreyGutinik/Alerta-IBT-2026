using AlertaIBT.Core.Holyrics;

namespace AlertaIBT.App.Tests.ViewModels;

// The "Parar a musica quando o cronometro chegar a N s" block of the Holyrics card: the two settings and the live chip.
public sealed class CountdownViewModelTests
{
    private static CountdownReading Shown(double seconds) => new(HolyricsOutcome.Ok, CountdownKind.Panel, seconds);

    [Fact]
    public void The_switch_is_on_and_the_seconds_are_two_on_a_first_run()
    {
        using var rig = new PresentationRig();

        Assert.True(rig.Vm.CountdownStopEnabled);
        Assert.Equal(2, rig.Vm.CountdownStopAt);
        Assert.Equal(Enumerable.Range(1, 10), rig.Vm.CountdownChoices);
        Assert.Contains("2 s", rig.Vm.CountdownTip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_switch_and_the_seconds_are_saved_as_the_last_position()
    {
        using var rig = new PresentationRig();

        rig.Vm.CountdownStopEnabled = false;
        rig.Vm.CountdownStopAt = 5;

        Assert.False(rig.Chrome.Settings.Current.Holyrics.CountdownStopEnabled);
        Assert.Equal(5, rig.Chrome.Settings.Current.Holyrics.CountdownStopAt);
        rig.Vm.CountdownStopAt = 99; // not a choice: refused
        Assert.Equal(5, rig.Chrome.Settings.Current.Holyrics.CountdownStopAt);
    }

    [Fact]
    public void A_saved_position_shows_on_the_screen_when_it_is_built()
    {
        var saved = new AlertaIBT.Core.Settings.AppSettings { Holyrics = new AlertaIBT.Core.Settings.HolyricsSettings { CountdownStopEnabled = false, CountdownStopAt = 7 } };
        using var rig = new PresentationRig(saved);

        Assert.False(rig.Vm.CountdownStopEnabled);
        Assert.Equal(7, rig.Vm.CountdownStopAt);
    }

    [Fact]
    public void The_live_chip_shows_the_time_only_while_a_countdown_is_shown()
    {
        using var rig = new PresentationRig();
        Assert.False(rig.Vm.HasCountdownLive);

        rig.CountdownSource.Next = Shown(7);
        rig.Look();
        Assert.True(rig.Vm.HasCountdownLive);
        Assert.Equal("00:07", rig.Vm.CountdownLiveText);

        rig.CountdownSource.Next = Shown(65);
        rig.Look();
        Assert.Equal("01:05", rig.Vm.CountdownLiveText);

        rig.CountdownSource.Next = new CountdownReading(HolyricsOutcome.Ok);
        rig.Look();
        Assert.False(rig.Vm.HasCountdownLive);
    }

    [Fact]
    public void Switching_the_watch_off_hides_the_chip_and_asks_nothing()
    {
        using var rig = new PresentationRig();
        rig.CountdownSource.Next = Shown(7);
        rig.Look();

        rig.Vm.CountdownStopEnabled = false;
        rig.Look();

        Assert.False(rig.Vm.HasCountdownLive);
        Assert.Equal(0, rig.Player.Pauses);
    }

    [Fact]
    public void A_token_without_permission_shows_its_reason_and_clears_when_it_works()
    {
        using var rig = new PresentationRig();

        rig.CountdownSource.Next = new CountdownReading(HolyricsOutcome.Unauthorized, Message: "token sem permiss\U000000E3o para GetCPInfo");
        rig.Look();
        Assert.True(rig.Vm.HasCountdownProblem);
        Assert.Equal("token sem permiss\U000000E3o para GetCPInfo", rig.Vm.CountdownProblem);

        rig.CountdownSource.Next = new CountdownReading(HolyricsOutcome.Ok);
        rig.Look();
        Assert.False(rig.Vm.HasCountdownProblem);
    }

    [Fact]
    public void A_countdown_reaching_the_chosen_seconds_pauses_the_player_through_the_screens_watcher_once()
    {
        using var rig = new PresentationRig();
        rig.Vm.CountdownStopAt = 4;

        foreach (var seconds in new double[] { 6, 5, 4, 3, 3, 2 })
        {
            rig.CountdownSource.Next = Shown(seconds);
            rig.Look();
        }

        Assert.Equal(1, rig.Player.Pauses);
    }
}

using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Loudness;
using AlertaIBT.Core.Player;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.App.Tests.ViewModels;

// "Igualar volume" in the player view-model: the menu, the toggle, the tag switch, the scan chip and the tooltip text.
public sealed class LoudnessViewModelTests
{
    [Fact]
    public void The_menu_has_the_seven_targets_and_Desligado_last()
    {
        Assert.Equal(
            ["-24 LUFS", "-23 LUFS", "-20 LUFS", "-18 LUFS", "-16 LUFS", "-14 LUFS", "-12 LUFS", "Desligado"],
            PlayerViewModel.NormalizeChoices.Select(c => c.Label));
        Assert.Equal(["-24", "-23", "-20", "-18", "-16", "-14", "-12", "off"], PlayerViewModel.NormalizeChoices.Select(c => c.Key));
    }

    [Fact]
    public void Defaults_are_on_minus_16_and_tags_off_and_the_menu_and_the_toggle_agree()
    {
        using var rig = new SomRig(withLoudness: true);
        var vm = rig.PlayerView;

        Assert.True(vm.HasLoudness);
        Assert.True(vm.NormalizeEnabled);
        Assert.Equal("-16", vm.NormalizeChoiceKey);
        Assert.False(vm.WriteLoudnessTags);

        vm.NormalizeChoiceKey = "-20";
        Assert.True(rig.Current.Player.NormalizeEnabled);
        Assert.Equal(-20, rig.Current.Player.NormalizeTargetLufs);

        vm.NormalizeChoiceKey = "off";
        Assert.False(rig.Current.Player.NormalizeEnabled);
        Assert.False(vm.NormalizeEnabled);
        Assert.Equal("off", vm.NormalizeChoiceKey);
        Assert.Equal(-20, rig.Current.Player.NormalizeTargetLufs); // the target is remembered

        vm.NormalizeEnabled = true; // the toggle brings the last target back
        Assert.Equal("-20", vm.NormalizeChoiceKey);

        vm.NormalizeChoiceKey = "-12";
        Assert.Equal(-12, rig.Current.Player.NormalizeTargetLufs);
        vm.WriteLoudnessTags = true;
        Assert.True(rig.Current.Player.WriteLoudnessTags);
    }

    [Fact]
    public void Without_the_loudness_service_there_is_no_row_and_no_chip()
    {
        using var rig = new SomRig();

        Assert.False(rig.PlayerView.HasLoudness);
        Assert.False(rig.PlayerView.ScanChipVisible);
    }

    [Fact]
    public void The_chip_counts_while_files_wait_and_then_goes_quiet()
    {
        using var rig = new SomRig(withLoudness: true);
        var vm = rig.PlayerView;
        Assert.False(vm.ScanChipVisible);

        rig.Loudness!.Scanner.Configure(Enumerable.Range(1, 237).Select(i => $"C:/no-such-folder/{i}.flac").ToList());

        Assert.True(vm.ScanChipVisible);
        Assert.Equal("medindo 0/237", vm.ScanChipText);
        Assert.Contains("segundo plano", vm.ScanChipTip, StringComparison.Ordinal);

        rig.Loudness.Scanner.Configure([]);
        Assert.False(vm.ScanChipVisible);
        Assert.Equal(string.Empty, vm.ScanChipText);
    }

    [Fact]
    public void The_tag_switch_tooltip_says_only_text_is_written_and_the_audio_is_untouched()
    {
        using var rig = new SomRig(withLoudness: true);

        Assert.Contains("S\U000000F3 texto \U000000E9 gravado", rig.PlayerView.WriteTagsTip, StringComparison.Ordinal);
        Assert.Contains("\U000000E1udio", rig.PlayerView.WriteTagsTip, StringComparison.Ordinal);
    }

    // ---- the tooltip of the toggle and the menu, with a track playing ----

    private sealed class FakeLoudness(Func<string, TrackLoudness?> answer) : ILoudnessProvider
    {
        public event Action<string>? Measured;

        public TrackLoudness? Get(string path) => answer(path);

        public void Request(string path)
        {
        }

        public void Raise(string path) => Measured?.Invoke(path);
    }

    private static string TipFor(Func<string, TrackLoudness?> answer, Func<PlayerSettings, PlayerSettings>? edit = null)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero));
        using var settings = new SettingsRig();
        using var library = new PlaylistLibrary(new FakeFiles(), _ => null, clock, action => action());
        var provider = new FakeLoudness(answer);
        using var player = new MusicPlayer(settings.Settings, library, new FakePlayerBackend(), new FakeAudio(), new FakeMetadata(), clock, null, action => action(), new Random(1), provider);
        using var alarms = new AlarmScheduler(settings.Settings, player, clock);
        player.Start();
        if (edit is not null)
        {
            settings.Settings.Update(s => s with { Player = edit(s.Player) });
        }

        using var vm = new PlayerViewModel(settings.Settings, player, alarms, new FakeAudio(), action => action(), action => action(), clock);
        player.Play();
        return vm.NormalizeTip;
    }

    [Fact]
    public void The_tooltip_shows_the_measured_loudness_and_the_applied_gain_in_Portuguese()
    {
        var tip = TipFor(_ => new TrackLoudness(-18.4, 0.2)); // +2.4 dB wanted, plenty of room

        Assert.Contains("-18,4 LUFS \U00002192 +2,4 dB", tip, StringComparison.Ordinal);
        Assert.DoesNotContain("limitado", tip, StringComparison.Ordinal);
        Assert.Contains("-16 LUFS", tip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tooltip_says_when_the_peak_limited_the_boost()
    {
        var tip = TipFor(_ => new TrackLoudness(-18.4, 0.9));

        Assert.Contains("-18,4 LUFS \U00002192 0,0 dB (limitado pelo pico)", tip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tooltip_of_a_loud_track_shows_the_cut()
    {
        var tip = TipFor(_ => new TrackLoudness(-9.0, 1.0));

        Assert.Contains("-9,0 LUFS \U00002192 -7,0 dB", tip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tooltip_of_an_unmeasured_track_says_it_plays_at_the_original_volume()
    {
        var tip = TipFor(_ => null);

        Assert.Contains("ainda n\U000000E3o foi medida", tip, StringComparison.Ordinal);
        Assert.Contains("volume original", tip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tooltip_when_off_says_the_files_play_as_they_are()
    {
        var tip = TipFor(_ => new TrackLoudness(-18.4, 0.2), p => p with { NormalizeEnabled = false });

        Assert.StartsWith("Igualar volume: desligado", tip, StringComparison.Ordinal);
    }
}

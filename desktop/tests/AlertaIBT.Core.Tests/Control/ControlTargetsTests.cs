using AlertaIBT.Core.Control;
using AlertaIBT.Core.Holyrics;
using AlertaIBT.Core.Player;

namespace AlertaIBT.Core.Tests.Control;

internal sealed class FakeControlNdi : IControlNdi
{
    public List<string> Calls { get; } = [];

    public bool LayerOn { get; set; }

    public int? PreviewSlot { get; set; } = 2;

    public int? ProgramSlot { get; set; } = 4;

    public int[] Positions { get; } = [50, 50, 30, 50, 80, 50];

    public ControlResult SelectPreview(int slot)
    {
        Calls.Add("preview " + slot);
        return ControlResult.Ok;
    }

    public ControlResult Take()
    {
        Calls.Add("take");
        return ControlResult.Ok;
    }

    public ControlResult SetLayer(bool on)
    {
        Calls.Add("layer " + on);
        LayerOn = on;
        return ControlResult.Ok;
    }

    public int GetVerticalPosition(int slot) => Positions[slot];

    public ControlResult SetVerticalPosition(int slot, int position)
    {
        Calls.Add($"vertical {slot} {position}");
        Positions[slot] = position;
        return ControlResult.Ok;
    }
}

internal sealed class FakePlayerControl : IPlayerControl
{
    public List<string> Calls { get; } = [];

    public PlayerState State { get; set; } = PlayerState.Stopped;

    public bool Fading { get; set; }

    public PlayerResult Result { get; set; } = PlayerResult.Ok;

    public PlayerResult Play() => Record("play");

    public PlayerResult Pause() => Record("pause");

    public PlayerResult Stop() => Record("stop");

    public PlayerResult Next() => Record("next");

    public PlayerResult Previous() => Record("previous");

    public PlayerStatus GetStatus() =>
        new(true, State, Fading, null, TimeSpan.Zero, TimeSpan.Zero, string.Empty, 0, 100, false, false, PlayerRepeat.Off, OutputStatus.Unknown, null);

    private PlayerResult Record(string call)
    {
        Calls.Add(call);
        return Result;
    }
}

internal sealed class FakeHolyricsRemote : IHolyricsRemote
{
    public List<HolyricsCommand> Posted { get; } = [];

    public bool IsConfigured { get; set; } = true;

    public bool ArrowsActive { get; set; } = true;

    public bool Post(HolyricsCommand command)
    {
        if (!IsConfigured)
        {
            return false;
        }

        Posted.Add(command);
        return true;
    }
}

internal sealed class FakeControlVolumes : IControlVolumes
{
    public Dictionary<ControlAction, double> Values { get; } = new()
    {
        [ControlAction.VolumeWindows] = 80,
        [ControlAction.VolumeHolyrics] = 100,
        [ControlAction.VolumePlayer] = 60,
    };

    public List<(ControlAction Action, int Volume)> Sets { get; } = [];

    public double? Get(ControlAction action) => Values.TryGetValue(action, out var v) ? v : null;

    public void Set(ControlAction action, int volume)
    {
        Sets.Add((action, volume));
        Values[action] = volume;
    }
}

// The actions really reach the controllers the buttons use, and the Holyrics ones answer "em breve".
public sealed class ControlTargetsTests
{
    private readonly FakeControlNdi _ndi = new();
    private readonly FakePlayerControl _player = new();
    private readonly FakeControlVolumes _volumes = new();
    private readonly ControlTargets _targets;

    public ControlTargetsTests()
    {
        _targets = new ControlTargets(_ndi, _player, _volumes);
    }

    private ControlResult Run(ControlAction action, ControlCommandKind kind = ControlCommandKind.Trigger, int parameter = 0, bool on = false, double value = 0) =>
        _targets.Run(new ControlCommand(1, action, kind, parameter, on, value));

    [Fact]
    public void Select_camera_puts_the_camera_in_preview_zero_based()
    {
        Run(ControlAction.SelectCamera, ControlCommandKind.Select, parameter: 3);

        Assert.Equal(["preview 2"], _ndi.Calls);
    }

    [Fact]
    public void A_camera_outside_one_to_six_is_refused()
    {
        Assert.False(Run(ControlAction.SelectCamera, ControlCommandKind.Select, parameter: 9).Success);
        Assert.Empty(_ndi.Calls);
    }

    [Fact]
    public void Camera_on_the_telao_follows_the_state_or_toggles()
    {
        Run(ControlAction.CameraOnTelao, ControlCommandKind.SetState, on: true);
        Run(ControlAction.CameraOnTelao, ControlCommandKind.SetState, on: false);
        Run(ControlAction.CameraOnTelao); // a trigger flips the layer
        Run(ControlAction.CameraOnTelao);

        Assert.Equal(["layer True", "layer False", "layer True", "layer False"], _ndi.Calls);
    }

    [Fact]
    public void Take_cuts()
    {
        Run(ControlAction.Take);

        Assert.Equal(["take"], _ndi.Calls);
    }

    [Theory]
    [InlineData(PlayerState.Stopped, false, "play")]
    [InlineData(PlayerState.Paused, false, "play")]
    [InlineData(PlayerState.Playing, false, "pause")]
    [InlineData(PlayerState.Playing, true, "play")] // fading out already: the same rule as the button
    public void Play_pause_toggles_like_the_button(PlayerState state, bool fading, string expected)
    {
        _player.State = state;
        _player.Fading = fading;

        Run(ControlAction.PlayPause);

        Assert.Equal([expected], _player.Calls);
    }

    [Fact]
    public void Stop_next_and_previous_reach_the_player()
    {
        Run(ControlAction.Stop);
        Run(ControlAction.Next);
        Run(ControlAction.Previous);

        Assert.Equal(["stop", "next", "previous"], _player.Calls);
    }

    [Fact]
    public void A_player_that_says_no_gives_its_reason()
    {
        _player.Result = PlayerResult.Disabled;

        var result = Run(ControlAction.Next);

        Assert.False(result.Success);
        Assert.Equal(PlayerResult.DisabledText, result.Message);
    }

    [Fact]
    public void The_vertical_faders_move_the_camera_of_preview_and_of_the_program()
    {
        Run(ControlAction.PreviewVertical, ControlCommandKind.SetLevel, value: 25.4);
        Run(ControlAction.ProgramVertical, ControlCommandKind.SetLevel, value: 90);

        Assert.Equal(["vertical 2 25", "vertical 4 90"], _ndi.Calls);
    }

    [Fact]
    public void A_vertical_fader_without_a_camera_says_so()
    {
        _ndi.PreviewSlot = null;

        var result = Run(ControlAction.PreviewVertical, ControlCommandKind.SetLevel, value: 50);

        Assert.False(result.Success);
        Assert.Empty(_ndi.Calls);
    }

    [Fact]
    public void The_volume_faders_set_the_volume_rows()
    {
        Run(ControlAction.VolumeWindows, ControlCommandKind.SetLevel, value: 49.6);
        Run(ControlAction.VolumeHolyrics, ControlCommandKind.SetLevel, value: 0);
        Run(ControlAction.VolumePlayer, ControlCommandKind.SetLevel, value: 120);

        Assert.Equal([(ControlAction.VolumeWindows, 50), (ControlAction.VolumeHolyrics, 0), (ControlAction.VolumePlayer, 100)], _volumes.Sets);
    }

    [Fact]
    public void The_levels_the_pickup_compares_are_the_apps_own()
    {
        Assert.Equal(30, _targets.GetLevel(ControlAction.PreviewVertical));
        Assert.Equal(80, _targets.GetLevel(ControlAction.ProgramVertical));
        Assert.Equal(80, _targets.GetLevel(ControlAction.VolumeWindows));
        Assert.Equal(100, _targets.GetLevel(ControlAction.VolumeHolyrics));
        Assert.Equal(60, _targets.GetLevel(ControlAction.VolumePlayer));
        Assert.Null(_targets.GetLevel(ControlAction.Take));

        _ndi.PreviewSlot = null;
        Assert.Null(_targets.GetLevel(ControlAction.PreviewVertical));
    }

    [Theory]
    [InlineData(ControlAction.SlideNext)]
    [InlineData(ControlAction.SlidePrevious)]
    [InlineData(ControlAction.VideoPlayPause)]
    [InlineData(ControlAction.LogoToggle)]
    public void Holyrics_actions_without_a_connection_say_so_and_send_nothing(ControlAction action)
    {
        var result = Run(action); // no Holyrics given

        Assert.False(result.Success);
        Assert.Equal(ControlTargets.HolyricsMissingText, result.Message);
    }

    [Fact]
    public void Holyrics_actions_are_queued_on_the_connection_and_return_at_once()
    {
        var holyrics = new FakeHolyricsRemote();
        var targets = new ControlTargets(_ndi, _player, _volumes, holyrics);

        ControlResult Go(ControlAction action, ControlCommandKind kind = ControlCommandKind.Trigger, bool on = false) =>
            targets.Run(new ControlCommand(13, action, kind, On: on));

        Assert.True(Go(ControlAction.SlideNext).Success);
        Assert.True(Go(ControlAction.SlidePrevious).Success);
        Assert.True(Go(ControlAction.VideoPlayPause).Success);
        Assert.True(Go(ControlAction.LogoToggle).Success); // Botao: a trigger flips the app's memory of the logo
        Assert.True(Go(ControlAction.LogoToggle, ControlCommandKind.SetState, on: true).Success); // Estado: the channel is the logo
        Assert.True(Go(ControlAction.LogoToggle, ControlCommandKind.SetState, on: false).Success);

        Assert.Equal(
            [
                new HolyricsCommand(HolyricsActionKind.SlideNext),
                new HolyricsCommand(HolyricsActionKind.SlidePrevious),
                new HolyricsCommand(HolyricsActionKind.VideoPlayPause),
                new HolyricsCommand(HolyricsActionKind.LogoToggle),
                new HolyricsCommand(HolyricsActionKind.LogoSet, true),
                new HolyricsCommand(HolyricsActionKind.LogoSet, false),
            ],
            holyrics.Posted);
        Assert.Empty(_ndi.Calls);
    }

    [Fact]
    public void Holyrics_actions_fail_clearly_when_the_connection_is_not_set_up()
    {
        var holyrics = new FakeHolyricsRemote { IsConfigured = false };
        var result = new ControlTargets(_ndi, _player, _volumes, holyrics).Run(new ControlCommand(13, ControlAction.SlideNext, ControlCommandKind.Trigger));

        Assert.False(result.Success);
        Assert.Equal(ControlTargets.HolyricsMissingText, result.Message);
        Assert.Empty(holyrics.Posted);
    }

    // The whole chain: packets in, controllers out, with the real service and the real engine.
    [Fact]
    public async Task A_console_packet_flow_drives_the_controllers_end_to_end()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ibt-control-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var settings = new AlertaIBT.Core.Settings.SettingsService(new AlertaIBT.Core.Settings.SettingsStore(Path.Combine(dir, "settings.json")));
        var source = new FakeArtNetSource();
        using var service = new ControlService(settings, _targets, () => source);
        service.Start();
        settings.Update(s => s with { Control = s.Control with { Enabled = true } });

        byte[] Frame(byte cut, byte layer, byte volume)
        {
            var data = new byte[512];
            data[7] = cut;
            data[6] = layer;
            data[20] = volume;
            return data;
        }

        void Send(byte[] data) => source.Raise(new ArtNetArrival(new ArtDmxPacket(0, 0, 99, data), System.Net.IPAddress.Loopback));

        _volumes.Values[ControlAction.VolumePlayer] = 60;
        Send(Frame(0, 255, 0)); // baseline: the layer is held ON but nothing happens
        await Task.Delay(60, TestContext.Current.CancellationToken);
        Assert.Empty(_ndi.Calls);

        Send(Frame(255, 255, 0)); // Cortar goes ON
        Send(Frame(255, 0, 0)); // the layer channel goes OFF
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Send(Frame(255, 0, 200)); // the fader crosses 60 % from below only at 200
        await Task.Delay(150, TestContext.Current.CancellationToken);

        Assert.Equal(["take", "layer False"], _ndi.Calls);
        Assert.Single(_volumes.Sets);
        Assert.Equal(78, _volumes.Sets[0].Volume);

        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
            // a late write of the settings
        }
    }
}

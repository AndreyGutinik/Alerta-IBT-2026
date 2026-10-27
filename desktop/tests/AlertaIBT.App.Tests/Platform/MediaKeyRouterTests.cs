using AlertaIBT.App.Platform;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.Core.Input;
using AlertaIBT.Core.Player;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.Platform;

// The media keys on the ONE shared keyboard hook, all fakes: nothing here touches the real keyboard or plays sound.
public sealed class MediaKeyRouterTests : IDisposable
{
    private sealed class RecordingPlayer : IPlayerControl
    {
        public List<string> Calls { get; } = [];

        public PlayerState State { get; set; } = PlayerState.Stopped;

        public PlayerResult Play() => Log("play");

        public PlayerResult Pause() => Log("pause");

        public PlayerResult Stop() => Log("stop");

        public PlayerResult Next() => Log("next");

        public PlayerResult Previous() => Log("previous");

        public PlayerStatus GetStatus() => new(
            true,
            State,
            false,
            null,
            TimeSpan.Zero,
            TimeSpan.Zero,
            "Igreja",
            0,
            80,
            false,
            true,
            PlayerRepeat.All,
            OutputStatus.Unknown,
            null);

        private PlayerResult Log(string call)
        {
            Calls.Add(call);
            return PlayerResult.Ok;
        }
    }

    private const int Ctrl = TripleCtrlDetector.VkLeftControl;

    private readonly FakeCursor _cursor = new();
    private readonly FakeKeyboardHook _hook = new();
    private readonly FakeHotkeyRegistrar _registrar = new();
    private readonly RecordingPlayer _player = new();
    private readonly List<MouseLockController> _controllers = [];
    private SettingsRig? _rig;

    public void Dispose()
    {
        foreach (var controller in _controllers)
        {
            controller.Dispose();
        }

        _rig?.Dispose();
    }

    private MediaKeyRouter Router(AppSettings? settings = null)
    {
        _rig = new SettingsRig(settings);
        return new MediaKeyRouter(_rig.Settings, _player, action => action(), action => action());
    }

    private MouseLockController Controller(AppSettings settings, out MediaKeyRouter router)
    {
        router = Router(settings);
        var controller = new MouseLockController(_rig!.Settings, new MouseLockService(_cursor), _hook, action => action(), null, router);
        _controllers.Add(controller);
        controller.Start(_registrar);
        return controller;
    }

    private static AppSettings With(Func<PlayerSettings, PlayerSettings> edit, bool tripleCtrl = true) =>
        new() { Player = edit(new PlayerSettings()), MouseLock = new MouseLockSettings { TripleCtrl = tripleCtrl } };

    // ---- the router ----

    [Fact]
    public void Each_media_key_goes_to_the_matching_command()
    {
        var router = Router();

        foreach (var key in new[] { MediaKeyDetector.VkPlayPause, MediaKeyDetector.VkNext, MediaKeyDetector.VkPrevious, MediaKeyDetector.VkStop })
        {
            router.Feed(key, true);
            router.Feed(key, false);
        }

        Assert.Equal(["play", "next", "previous", "stop"], _player.Calls);
    }

    [Fact]
    public void Play_pause_pauses_while_playing()
    {
        var router = Router();
        _player.State = PlayerState.Playing;

        router.Feed(MediaKeyDetector.VkPlayPause, true);

        Assert.Equal(["pause"], _player.Calls);
    }

    [Fact]
    public void A_held_key_is_one_command_and_other_keys_do_nothing()
    {
        var router = Router();

        router.Feed(MediaKeyDetector.VkNext, true);
        router.Feed(MediaKeyDetector.VkNext, true);
        router.Feed(MediaKeyDetector.VkNext, true);
        router.Feed(0x41, true);
        router.Feed(Ctrl, true);

        Assert.Equal(["next"], _player.Calls);
    }

    [Fact]
    public void With_the_option_off_or_the_player_off_the_keys_are_ignored()
    {
        var off = Router(With(p => p with { MediaKeys = false }));
        off.Feed(MediaKeyDetector.VkNext, true);
        Assert.False(off.Wanted);

        var disabled = Router(With(p => p with { Enabled = false }));
        disabled.Feed(MediaKeyDetector.VkNext, true);
        Assert.False(disabled.Wanted);

        Assert.Empty(_player.Calls);
    }

    [Fact]
    public void The_command_is_posted_to_the_ui_and_then_run_off_it()
    {
        _rig = new SettingsRig();
        var steps = new List<string>();
        var router = new MediaKeyRouter(
            _rig.Settings,
            _player,
            action =>
            {
                steps.Add("post");
                action();
            },
            action =>
            {
                steps.Add("run");
                action();
            });

        router.Feed(MediaKeyDetector.VkStop, true);

        Assert.Equal(["post", "run"], steps);
        Assert.Equal(["stop"], _player.Calls);
    }

    // ---- the one hook ----

    [Fact]
    public void The_media_keys_ride_the_same_hook_and_work_with_Ctrl_x3_off()
    {
        Controller(With(p => p, tripleCtrl: false), out _);

        Assert.True(_hook.IsInstalled);
        Assert.Equal(1, _hook.Installs);
        _hook.Tap(MediaKeyDetector.VkNext, 100);
        Assert.Equal(["next"], _player.Calls);
    }

    [Fact]
    public void Ctrl_x3_still_recenters_and_the_same_hook_still_hears_the_media_keys()
    {
        Controller(With(p => p), out _);

        _hook.Tap(Ctrl, 100);
        _hook.Tap(Ctrl, 300);
        _hook.Tap(Ctrl, 500);
        _hook.Tap(MediaKeyDetector.VkPlayPause, 700);

        Assert.Single(_cursor.Moves);
        Assert.Equal(["play"], _player.Calls);
        Assert.Equal(1, _hook.Installs);
    }

    [Fact]
    public void With_Ctrl_x3_off_pressing_Ctrl_three_times_moves_nothing()
    {
        Controller(With(p => p, tripleCtrl: false), out _);

        _hook.Tap(Ctrl, 100);
        _hook.Tap(Ctrl, 300);
        _hook.Tap(Ctrl, 500);

        Assert.Empty(_cursor.Moves);
    }

    [Fact]
    public void No_hook_is_installed_when_neither_Ctrl_x3_nor_the_media_keys_are_wanted()
    {
        Controller(With(p => p with { MediaKeys = false }, tripleCtrl: false), out _);

        Assert.False(_hook.IsInstalled);
        Assert.Equal(0, _hook.Installs);
    }

    [Fact]
    public void The_hook_follows_the_media_key_option_and_the_player_switch_while_the_app_runs()
    {
        var controller = Controller(With(p => p with { MediaKeys = false }, tripleCtrl: false), out _);
        Assert.False(_hook.IsInstalled);

        _rig!.Settings.Update(s => s with { Player = s.Player with { MediaKeys = true } });
        controller.ApplySettings(_rig.Settings.Current.MouseLock);
        Assert.True(_hook.IsInstalled);

        _rig.Settings.Update(s => s with { Player = s.Player with { Enabled = false } });
        controller.ApplySettings(_rig.Settings.Current.MouseLock);
        Assert.False(_hook.IsInstalled);
    }
}

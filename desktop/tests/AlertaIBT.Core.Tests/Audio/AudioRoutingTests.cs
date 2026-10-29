using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Tests.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Audio;

/// <summary>The Windows per-app output for the tests: plain data, every call recorded, nothing here can touch a real program.</summary>
public sealed class FakeAppOutput : IAppOutputPolicy
{
    public Dictionary<int, string> Endpoints { get; } = [];

    public List<string> Calls { get; } = [];

    public bool FailSet { get; set; }

    public string? GetPersistedEndpoint(int processId)
    {
        Calls.Add("get:" + processId);
        return Endpoints.TryGetValue(processId, out var id) ? id : string.Empty;
    }

    public bool SetPersistedEndpoint(int processId, string? endpointId)
    {
        Calls.Add($"set:{processId}:{endpointId}");
        if (FailSet)
        {
            return false;
        }

        if (endpointId is null)
        {
            Endpoints.Remove(processId);
        }
        else
        {
            Endpoints[processId] = endpointId;
        }

        return true;
    }

    public IEnumerable<string> Sets => Calls.Where(c => c.StartsWith("set:", StringComparison.Ordinal));
}

// The output rules: id, then name rule, never a stand-in; and the Holyrics per-app output against fakes (never a real program).
public sealed class AudioRoutingTests : IDisposable
{
    private const string WingId = FakeAudioSystem.WingId;
    private const string HolyId = "{0.0.0.00000000}.{wing-12}";
    private const string HolyName = "Alto-falantes (WING-USB Out 1-2)";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-routing-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeAudioSystem _audio = new();
    private readonly FakeAppOutput _output = new();
    private readonly CapturingLogger _log = new();
    private readonly SettingsService _settings;
    private AudioController? _controller;

    public AudioRoutingTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsService(new SettingsStore(Path.Combine(_dir, "settings.json"), _clock), _clock);
        _audio.Devices.Add(new AudioDevice(HolyId, HolyName));
    }

    public void Dispose()
    {
        _controller?.Dispose();
        _settings.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A scanner may hold a file for a moment.
        }
    }

    private AudioController Boot(bool withPolicy = true)
    {
        _controller = new AudioController(_audio, _settings, _clock, _log, withPolicy ? _output : null, action => action());
        _controller.Start();
        _clock.Advance(AudioController.StartDelay + AudioController.Debounce);
        return _controller;
    }

    private void Wait(TimeSpan span) => _clock.Advance(span);

    private void Change(Func<AudioSettings, AudioSettings> edit) => _settings.Update(s => s with { Audio = edit(s.Audio) });

    // ---- the rules ----

    [Fact]
    public void A_name_rule_needs_every_word_in_any_case_and_ignores_the_2_prefix_windows_adds()
    {
        var rule = DeviceMatchRule.Parse("WING + 1-2");

        Assert.True(rule.Matches("WING-USB (Out 1-2)"));
        Assert.True(rule.Matches("wing usb out 1-2"));
        Assert.False(rule.Matches("WING-USB (Out 3-4)"));
        Assert.False(rule.Matches("Speakers 1-2"));
        Assert.True(new DeviceMatchRule(["Alto-falantes (WING USB)"]).Matches("Alto-falantes (2- WING USB)"));
        Assert.False(DeviceMatchRule.None.Matches("anything"));
    }

    [Fact]
    public void Find_takes_the_saved_id_first_then_the_first_active_device_of_the_rule_and_never_a_stand_in()
    {
        IReadOnlyList<AudioDevice> active = [new("a", "Speakers"), new("b", "WING-USB (Out 1-2)"), new("c", "WING-USB (Out 1-2) copy")];
        var rule = DeviceMatchRule.Parse("WING + 1-2");

        Assert.Equal(new OutputChoice(OutputChoiceKind.ById, active[2]), OutputResolver.Find(active, "c", rule));
        Assert.Equal(new OutputChoice(OutputChoiceKind.ByRule, active[1]), OutputResolver.Find(active, "gone", rule));
        Assert.Equal(new OutputChoice(OutputChoiceKind.ByRule, active[1]), OutputResolver.Find(active, null, rule));
        Assert.Equal(new OutputChoice(OutputChoiceKind.None, null), OutputResolver.Find([active[0]], "gone", rule));
    }

    [Fact]
    public void The_per_app_device_path_has_the_mmdevapi_form_and_round_trips_to_the_endpoint_id()
    {
        var path = MmdevapiDevicePath.ForRender("{0.0.0.00000000}.{abc-1}");

        Assert.Equal(@"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{abc-1}#{e6327cad-dcec-4949-ae8a-991e976a79d2}", path);
        Assert.Equal("{0.0.0.00000000}.{abc-1}", MmdevapiDevicePath.ToEndpointId(path));
        Assert.Equal("{0.0.0.00000000}.{abc-1}", MmdevapiDevicePath.ToEndpointId("{0.0.0.00000000}.{abc-1}"));
        Assert.Equal(string.Empty, MmdevapiDevicePath.ToEndpointId(null));
        Assert.Equal(string.Empty, MmdevapiDevicePath.ToEndpointId("  "));
    }

    [Fact]
    public void The_new_audio_settings_have_the_church_defaults_and_survive_a_save_and_a_load()
    {
        var defaults = new AudioSettings();
        Assert.Equal(["WING"], defaults.OutputMatch);
        Assert.Equal(["WING", "1-2"], defaults.HolyricsMatch);
        Assert.True(defaults.HolyricsRoute);

        var custom = new AppSettings { Audio = defaults with { HolyricsRoute = false, HolyricsDeviceId = "id-1", HolyricsDeviceName = "Out", HolyricsMatch = ["Out", "9"], OutputMatch = ["Mesa"] } };
        var (loaded, corrupt) = SettingsJson.Load(SettingsJson.Save(custom));

        Assert.False(corrupt);
        Assert.False(loaded.Audio.HolyricsRoute);
        Assert.Equal("id-1", loaded.Audio.HolyricsDeviceId);
        Assert.Equal("Out", loaded.Audio.HolyricsDeviceName);
        Assert.Equal(["Out", "9"], loaded.Audio.HolyricsMatch);
        Assert.Equal(["Mesa"], loaded.Audio.OutputMatch);

        var (old, _) = SettingsJson.Load("""{ "audio": { "masterVolume": 70 } }""");
        Assert.Equal(["WING", "1-2"], old.Audio.HolyricsMatch);
        Assert.True(old.Audio.HolyricsRoute);
        Assert.Equal(100, old.Audio.Apps[0].Volume);
        Assert.Equal(100, new AppSettings().Player.Volume);
    }

    // ---- Holyrics per-app output ----

    [Fact]
    public void A_holyrics_process_that_makes_sound_is_sent_to_its_output_once_and_the_id_is_remembered()
    {
        _audio.AddSession(WingId, "s-holy", "javaw", raise: false, processId: 100);
        _audio.AddSession(WingId, "s-chrome", "chrome", raise: false, processId: 200);

        Boot();

        Assert.Equal([$"set:100:{HolyId}"], _output.Sets);
        Assert.Equal(HolyId, _settings.Current.Audio.HolyricsDeviceId);
        Assert.Equal(HolyName, _settings.Current.Audio.HolyricsDeviceName);
        Assert.Equal("WING-USB Out 1-2 \U00002014 vale na pr\U000000F3xima vez que o Holyrics tocar", _controller!.Status.HolyricsText);
        Assert.Contains(_log.Entries, e => e.Message.Contains("Holyrics (processo 100) agora toca em", StringComparison.Ordinal));

        _output.Calls.Clear();
        _audio.Add(new AudioDevice("{x}", "Fones (USB)"));
        Wait(AudioController.Debounce);

        Assert.Empty(_output.Calls); // not read again, not set again
    }

    [Fact]
    public void A_process_that_starts_later_is_routed_when_its_session_appears()
    {
        Boot();
        Assert.Empty(_output.Calls);
        Assert.Equal("WING-USB Out 1-2 \U00002014 aguardando o Holyrics tocar", _controller!.Status.HolyricsText);

        _audio.AddSession(WingId, "s-holy", "Holyrics", processId: 321);
        Wait(AudioController.Debounce);

        Assert.Equal([$"set:321:{HolyId}"], _output.Sets);
    }

    [Fact]
    public void A_restarted_holyrics_is_routed_again_because_it_is_a_new_process()
    {
        _audio.AddSession(WingId, "s-1", "javaw", raise: false, processId: 100);
        Boot();

        _audio.Sessions[WingId].Clear();
        _audio.AddSession(WingId, "s-2", "javaw", processId: 101);
        Wait(AudioController.Debounce);

        Assert.Equal([$"set:100:{HolyId}", $"set:101:{HolyId}"], _output.Sets);
    }

    [Fact]
    public void The_status_says_it_counts_from_the_next_time_while_the_live_sound_is_still_elsewhere_and_then_that_it_moved()
    {
        _audio.AddSession(WingId, "s-holy", "javaw", raise: false, processId: 100);

        Boot();

        Assert.Equal("WING-USB Out 1-2 — vale na próxima vez que o Holyrics tocar", _controller!.Status.HolyricsText); // the program plays on another output: Windows will use the new one next time

        _audio.Sessions[WingId].Clear();
        _audio.AddSession(HolyId, "s-holy", "javaw", raise: false, processId: 100); // the program opened a new stream and Windows sent it to the new output
        Wait(AudioController.VerifyDelay);

        Assert.Equal("Holyrics toca em WING-USB Out 1-2", _controller.Status.HolyricsText);
    }

    [Fact]
    public void Changing_the_holyrics_output_sets_the_program_at_once_without_the_half_second_look()
    {
        _audio.AddSession(WingId, "s-holy", "javaw", raise: false, processId: 100);
        Boot();
        _output.Calls.Clear();
        var other = new AudioDevice("{0.0.0.00000000}.{other-out}", "Outra (USB)");
        _audio.Add(other);
        Wait(AudioController.Debounce);
        _output.Calls.Clear();

        _settings.Update(s => s with { Audio = s.Audio with { HolyricsDeviceId = other.Id, HolyricsDeviceName = other.Name, HolyricsMatch = [other.Name] } });

        Assert.Contains($"set:100:{other.Id}", _output.Sets); // no clock moved: it did not wait for the debounce
    }

    [Fact]
    public void Changing_the_windows_default_lock_target_corrects_the_default_at_once()
    {
        Boot();
        var other = new AudioDevice("{0.0.0.00000000}.{other-out}", "Outra (USB)");
        _audio.Add(other);
        Wait(AudioController.Debounce);
        _audio.Calls.Clear();

        _settings.Update(s => s with { Audio = s.Audio with { OutputDeviceId = other.Id, OutputDeviceName = other.Name, OutputMatch = [other.Name] } });

        Assert.Contains($"default:{other.Id}", _audio.Calls); // no clock moved
    }

    [Fact]
    public void A_program_already_on_the_right_output_is_not_set_again()
    {
        _output.Endpoints[100] = HolyId.ToUpperInvariant();
        _audio.AddSession(WingId, "s-holy", "javaw", raise: false, processId: 100);

        Boot();

        Assert.Empty(_output.Sets);
        Assert.Equal("Holyrics toca em WING-USB Out 1-2", _controller!.Status.HolyricsText);
    }

    [Fact]
    public void Apply_now_looks_again_and_sets_what_was_changed_by_hand_in_windows()
    {
        _audio.AddSession(WingId, "s-holy", "javaw", raise: false, processId: 100);
        Boot();
        _output.Calls.Clear();

        _output.Endpoints[100] = "{somewhere-else}"; // the owner picked another device in the Windows mixer
        Wait(AudioController.Debounce);
        Assert.Empty(_output.Calls); // a look by itself leaves it alone

        _controller!.ApplyNow();

        Assert.Equal([$"set:100:{HolyId}"], _output.Sets);
    }

    [Fact]
    public void With_its_switch_off_the_per_app_output_is_never_read_or_written_not_even_by_apply_now()
    {
        Change(a => a with { HolyricsRoute = false });
        _audio.AddSession(WingId, "s-holy", "javaw", raise: false, processId: 100);

        Boot();
        _audio.AddSession(WingId, "s-holy-2", "javaw", processId: 101);
        Wait(AudioController.Debounce);
        _controller!.ApplyNow();

        Assert.Empty(_output.Calls);
        Assert.Null(_controller.Status.HolyricsText);
    }

    [Fact]
    public void It_does_not_depend_on_the_adjust_at_start_switch()
    {
        Change(a => a with { Enabled = false });
        _audio.AddSession(WingId, "s-holy", "javaw", raise: false, processId: 100);

        Boot();

        Assert.Equal([$"set:100:{HolyId}"], _output.Sets);
        Assert.Empty(_audio.Writes("session")); // the volumes still wait for their own switch
    }

    [Fact]
    public void With_no_device_for_the_rule_holyrics_is_left_alone_and_the_status_says_so()
    {
        _audio.Remove(HolyId);
        _audio.AddSession(WingId, "s-holy", "javaw", raise: false, processId: 100);

        Boot();

        Assert.Empty(_output.Calls);
        Assert.Equal("WING + 1-2 \U00002014 desconectado, deixando o Holyrics como est\U000000E1", _controller!.Status.HolyricsText);
        Assert.Single(_log.Entries, e => e.Message.Contains("fica como esta", StringComparison.Ordinal));
    }

    [Fact]
    public void A_remembered_but_unplugged_output_shows_its_name_and_is_left_alone_until_it_returns()
    {
        _audio.AddSession(WingId, "s-holy", "javaw", raise: false, processId: 100);
        Boot();
        _output.Calls.Clear();

        _audio.Remove(HolyId);
        Wait(AudioController.Debounce);
        Assert.Equal("WING-USB Out 1-2 \U00002014 desconectado, deixando o Holyrics como est\U000000E1", _controller!.Status.HolyricsText);
        Assert.Empty(_output.Calls);

        _audio.Add(new AudioDevice(HolyId, HolyName));
        Wait(AudioController.Debounce);

        Assert.Equal("WING-USB Out 1-2 \U00002014 vale na pr\U000000F3xima vez que o Holyrics tocar", _controller.Status.HolyricsText);
    }

    [Fact]
    public void The_output_is_found_by_its_rule_when_the_saved_id_is_not_active_and_the_new_id_is_saved()
    {
        Change(a => a with { HolyricsDeviceId = "{old-id}", HolyricsDeviceName = HolyName });

        Boot();

        Assert.Equal(HolyId, _settings.Current.Audio.HolyricsDeviceId);
    }

    [Fact]
    public void A_refused_change_is_logged_once_and_retried_only_by_apply_now()
    {
        _output.FailSet = true;
        _audio.AddSession(WingId, "s-holy", "javaw", raise: false, processId: 100);
        Boot();

        _audio.Add(new AudioDevice("{x}", "Fones (USB)"));
        Wait(AudioController.Debounce);

        Assert.Single(_output.Sets);
        Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("mudar a saida do Holyrics", StringComparison.Ordinal));
        Assert.Contains("n\U000000E3o foi poss\U000000EDvel mudar", _controller!.Status.HolyricsText);

        _output.FailSet = false;
        _controller.ApplyNow();

        Assert.Equal(2, _output.Sets.Count());
        Assert.Equal("WING-USB Out 1-2 \U00002014 vale na pr\U000000F3xima vez que o Holyrics tocar", _controller.Status.HolyricsText);
    }

    [Fact]
    public void Without_the_windows_interface_nothing_breaks_and_the_status_says_there_is_no_access()
    {
        _audio.AddSession(WingId, "s-holy", "javaw", raise: false, processId: 100);

        Boot(withPolicy: false);

        Assert.Contains("sem acesso", _controller!.Status.HolyricsText);
        Assert.Empty(_output.Calls);
    }

    [Fact]
    public void Only_holyrics_processes_are_routed_and_the_system_sounds_never()
    {
        _audio.AddSession(WingId, "s-sys", null, system: true, raise: false);
        _audio.AddSession(WingId, "s-chrome", "chrome", raise: false, processId: 200);
        _audio.AddSession(WingId, "s-spot", "Spotify", raise: false, processId: 300);

        Boot();

        Assert.Empty(_output.Calls);
    }

    [Fact]
    public void Both_the_default_output_and_the_holyrics_output_are_watched_and_the_volume_is_set_on_both()
    {
        _audio.AddSession(HolyId, "h-1", "javaw", raise: false, processId: 100);
        _audio.AddSession(WingId, "w-sys", null, system: true, raise: false);

        Boot();

        Assert.Equal([WingId, HolyId], _audio.WatchedIds);
        Assert.Contains($"session:{HolyId}:h-1:100:False", _audio.Calls);
        Assert.Contains($"session:{WingId}:w-sys:0:False", _audio.Calls);
        Assert.Equal([$"set:100:{HolyId}"], _output.Sets); // the process was found on the Holyrics output too
    }

    [Fact]
    public void The_master_volume_is_set_only_on_the_default_output_not_on_the_holyrics_output()
    {
        Boot();

        Assert.Equal([$"master:{WingId}:80:False"], _audio.Writes("master"));
    }
}

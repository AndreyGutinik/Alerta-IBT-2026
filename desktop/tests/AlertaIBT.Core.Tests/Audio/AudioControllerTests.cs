using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Tests.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Audio;

// The rules of the Som tab against a fake Windows audio: nothing here can change the real volume or output.
public sealed class AudioControllerTests : IDisposable
{
    private const string WingId = FakeAudioSystem.WingId;
    private const string SpeakersId = FakeAudioSystem.SpeakersId;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-audio-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeAudioSystem _audio = new();
    private readonly CapturingLogger _log = new();
    private readonly SettingsService _settings;
    private readonly AudioController _controller;

    public AudioControllerTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsService(new SettingsStore(Path.Combine(_dir, "settings.json"), _clock), _clock);
        _controller = new AudioController(_audio, _settings, _clock, _log);
    }

    public void Dispose()
    {
        _controller.Dispose();
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

    private void Change(Func<AudioSettings, AudioSettings> edit) =>
        _settings.Update(s => s with { Audio = edit(s.Audio) });

    private void Wait(TimeSpan span) => _clock.Advance(span);

    private void Boot()
    {
        _controller.Start();
        Wait(AudioController.StartDelay + AudioController.Debounce);
    }

    private IEnumerable<string> LogLines => _log.Entries.Select(e => e.Message);

    private int VolumeWrites => _audio.Calls.Count(c => c.StartsWith("master", StringComparison.Ordinal) || c.StartsWith("session", StringComparison.Ordinal));

    // ---- start ----

    [Fact]
    public void Nothing_is_read_or_written_when_it_starts_and_no_volume_is_set_before_the_delay()
    {
        _controller.Start();

        Assert.Empty(_audio.Calls);

        Wait(TimeSpan.FromSeconds(1)); // the first look may select the output, never set a volume yet
        Assert.Empty(_audio.Writes("master"));
        Assert.Empty(_audio.Writes("session"));
    }

    [Fact]
    public void At_start_the_output_is_selected_and_the_master_is_80_percent_unmuted_on_it()
    {
        Boot();

        Assert.Equal([$"master:{WingId}:80:False"], _audio.Writes("master"));
        Assert.Equal(WingId, _audio.DefaultConsole);
        Assert.Equal(WingId, _audio.DefaultMultimedia);
        Assert.Equal(WingId, _audio.DefaultCommunications);
    }

    [Fact]
    public void Sessions_that_already_exist_get_their_volume_once_and_the_system_sounds_get_zero()
    {
        _audio.AddSession(WingId, "s-sys", null, system: true, raise: false);
        _audio.AddSession(WingId, "s-holy", "javaw", raise: false);
        _audio.AddSession(WingId, "s-spot", "Spotify", raise: false);
        _audio.AddSession(WingId, "s-other", "chrome", raise: false);

        Boot();

        Assert.Equal(
            [$"session:{WingId}:s-sys:0:False", $"session:{WingId}:s-holy:100:False", $"session:{WingId}:s-spot:100:False"],
            _audio.Writes("session"));
    }

    [Fact]
    public void The_volumes_come_from_the_settings_and_a_program_can_start_muted()
    {
        Change(a => a with
        {
            MasterVolume = 55,
            MasterMuted = true,
            SystemSoundsVolume = 10,
            Apps = [new AudioAppEntry("Spotify", ["spotify.exe"], 30, true)],
        });
        _audio.AddSession(WingId, "s-sys", null, system: true, raise: false);
        _audio.AddSession(WingId, "s-spot", "Spotify", raise: false);

        Boot();

        Assert.Equal([$"master:{WingId}:55:True"], _audio.Writes("master"));
        Assert.Contains($"session:{WingId}:s-spot:30:True", _audio.Calls);
        Assert.Contains($"session:{WingId}:s-sys:10:False", _audio.Calls);
    }

    [Fact]
    public void With_the_switch_off_no_volume_is_set_but_the_output_is_still_locked()
    {
        Change(a => a with { Enabled = false });
        _audio.AddSession(WingId, "s-spot", "Spotify", raise: false);

        Boot();

        Assert.Empty(_audio.Writes("master"));
        Assert.Empty(_audio.Writes("session"));
        Assert.Equal(WingId, _audio.DefaultConsole);
        Assert.Null(_audio.Watching);
    }

    // ---- never re-applied ----

    [Fact]
    public void Later_events_never_apply_the_master_or_a_session_again()
    {
        _audio.AddSession(WingId, "s-spot", "Spotify", raise: false);
        Boot();
        var before = VolumeWrites;

        _audio.Add(new AudioDevice("{x}", "Fones (USB)"));
        Wait(AudioController.Debounce);
        Change(a => a with { MasterVolume = 20 }); // the owner moves a slider
        Wait(AudioController.Debounce);
        Wait(TimeSpan.FromMinutes(5));

        Assert.Equal(before, VolumeWrites);
    }

    // ---- sessions that appear later ----

    [Fact]
    public void A_program_that_opens_inside_the_first_30_minutes_gets_its_volume_once()
    {
        Boot();
        Wait(TimeSpan.FromMinutes(10));

        _audio.AddSession(WingId, "s-spot", "Spotify");
        Wait(AudioController.Debounce);
        _audio.AddSession(WingId, "s-other", "chrome");
        Wait(AudioController.Debounce);

        Assert.Equal([$"session:{WingId}:s-spot:100:False"], _audio.Writes("session"));
    }

    [Fact]
    public void A_program_that_opens_after_30_minutes_is_left_alone()
    {
        Boot();
        Wait(AudioController.SessionWindow + TimeSpan.FromMinutes(1));

        _audio.AddSession(WingId, "s-spot", "Spotify");
        Wait(AudioController.Debounce);

        Assert.Empty(_audio.Writes("session"));
    }

    [Fact]
    public void The_system_sounds_session_is_set_whenever_it_appears()
    {
        Boot();
        Wait(AudioController.SessionWindow + TimeSpan.FromHours(2));

        _audio.AddSession(WingId, "s-sys", null, system: true);
        Wait(AudioController.Debounce);

        Assert.Equal([$"session:{WingId}:s-sys:0:False"], _audio.Writes("session"));
    }

    [Fact]
    public void A_program_closed_and_opened_again_inside_the_window_is_set_again_because_it_is_a_new_session()
    {
        Boot();

        _audio.AddSession(WingId, "spot-1", "Spotify");
        Wait(AudioController.Debounce);
        _audio.Sessions[WingId].Clear();
        _audio.AddSession(WingId, "spot-2", "Spotify");
        Wait(AudioController.Debounce);

        Assert.Equal([$"session:{WingId}:spot-1:100:False", $"session:{WingId}:spot-2:100:False"], _audio.Writes("session"));
    }

    [Fact]
    public void Process_names_match_with_any_case_with_or_without_exe_and_any_name_of_the_list()
    {
        Boot();

        _audio.AddSession(WingId, "a", "SPOTIFY");
        _audio.AddSession(WingId, "b", "javaw");
        _audio.AddSession(WingId, "c", "holyrics");
        Wait(AudioController.Debounce);

        Assert.Equal(3, _audio.Writes("session").Count());
    }

    // ---- apply now ----

    [Fact]
    public void Apply_now_sets_everything_again_even_after_30_minutes_and_with_the_switch_off()
    {
        _audio.AddSession(WingId, "s-spot", "Spotify", raise: false);
        _audio.AddSession(WingId, "s-sys", null, system: true, raise: false);
        Boot();
        Change(a => a with { Enabled = false, MasterVolume = 42 });
        Wait(AudioController.SessionWindow + TimeSpan.FromMinutes(5));
        _audio.Calls.Clear();

        _controller.ApplyNow();

        Assert.Equal([$"master:{WingId}:42:False"], _audio.Writes("master"));
        Assert.Contains($"session:{WingId}:s-spot:100:False", _audio.Calls);
        Assert.Contains($"session:{WingId}:s-sys:0:False", _audio.Calls);
        Assert.Contains("Volume geral 42%", _controller.Status.AppliedSummary);
    }

    [Fact]
    public void Apply_now_with_the_output_missing_does_nothing_and_does_not_throw()
    {
        _audio.Remove(WingId);
        Boot();
        _audio.Calls.Clear();

        _controller.ApplyNow();

        Assert.Empty(_audio.Writes("master"));
        Assert.Empty(_audio.Writes("default"));
    }

    // ---- lock ----

    [Fact]
    public void When_windows_switches_the_output_it_is_put_back_after_the_debounce_and_logged()
    {
        Boot();
        _audio.Calls.Clear();

        _audio.WindowsSwitchesTo(SpeakersId);
        Wait(TimeSpan.FromMilliseconds(400));
        Assert.Empty(_audio.Writes("default"));

        Wait(TimeSpan.FromMilliseconds(150));

        Assert.Equal([$"default:{WingId}"], _audio.Writes("default"));
        Assert.Equal(WingId, _audio.DefaultCommunications);
        Assert.Contains("Saida de audio: voltou para WING USB", LogLines);
    }

    [Fact]
    public void A_change_of_only_one_role_is_corrected_too()
    {
        Boot();
        _audio.Calls.Clear();

        _audio.DefaultCommunications = SpeakersId;
        _audio.WindowsSwitchesTo(WingId); // raises the event and sets all three
        _audio.DefaultCommunications = SpeakersId; // now only the communications role differs
        Wait(AudioController.Debounce);

        Assert.Equal([$"default:{WingId}"], _audio.Writes("default"));
    }

    [Fact]
    public void A_fight_with_a_driver_stops_after_three_corrections_in_a_minute_and_logs_once()
    {
        Boot();
        _audio.Calls.Clear();
        _audio.AfterSetDefault = () => _audio.DefaultConsole = _audio.DefaultMultimedia = _audio.DefaultCommunications = SpeakersId;

        for (var i = 0; i < 8; i++)
        {
            _audio.WindowsSwitchesTo(SpeakersId);
            Wait(AudioController.Debounce);
        }

        Assert.Equal(AudioController.MaxCorrections, _audio.Writes("default").Count());
        Assert.Single(LogLines, l => l.Contains("parei de corrigir", StringComparison.Ordinal));
    }

    [Fact]
    public void After_a_minute_the_lock_corrects_again()
    {
        Boot();
        _audio.AfterSetDefault = () => _audio.DefaultConsole = _audio.DefaultMultimedia = _audio.DefaultCommunications = SpeakersId;
        for (var i = 0; i < 5; i++)
        {
            _audio.WindowsSwitchesTo(SpeakersId);
            Wait(AudioController.Debounce);
        }

        _audio.Calls.Clear();
        Wait(AudioController.CorrectionWindow);
        _audio.WindowsSwitchesTo(SpeakersId);
        Wait(AudioController.Debounce);

        Assert.Single(_audio.Writes("default"));
    }

    [Fact]
    public void With_the_lock_off_the_output_is_never_touched_and_the_master_goes_to_the_current_default()
    {
        Change(a => a with { LockOutput = false });

        Boot();
        _audio.WindowsSwitchesTo(WingId);
        Wait(AudioController.Debounce);
        _audio.WindowsSwitchesTo(SpeakersId);
        Wait(AudioController.Debounce);

        Assert.Empty(_audio.Writes("default"));
        Assert.Equal([$"master:{SpeakersId}:80:False"], _audio.Writes("master")); // set once at start, to what was default then
    }

    [Fact]
    public void Turning_the_lock_on_puts_the_output_back_at_once_and_turning_it_off_stops_fighting()
    {
        Change(a => a with { LockOutput = false });
        Boot();
        _audio.Calls.Clear();

        Change(a => a with { LockOutput = true });
        Wait(AudioController.Debounce);
        Assert.Equal([$"default:{WingId}"], _audio.Writes("default"));

        Change(a => a with { LockOutput = false });
        Wait(AudioController.Debounce);
        _audio.WindowsSwitchesTo(SpeakersId);
        Wait(AudioController.Debounce);
        Assert.Single(_audio.Writes("default"));
    }

    [Fact]
    public void An_output_that_was_never_found_is_not_forced_and_the_status_says_nothing_is_chosen()
    {
        _audio.Remove(WingId);

        Boot();

        Assert.Empty(_audio.Writes("default"));
        Assert.Empty(_audio.Writes("master"));
        Assert.Null(_controller.Status.SelectedName);
    }

    [Fact]
    public void A_saved_output_that_is_unplugged_waits_and_comes_back_selected_with_its_volume()
    {
        Boot(); // the WING is found and saved
        _audio.Calls.Clear();

        _audio.Remove(WingId);
        Wait(AudioController.Debounce);
        Assert.Equal("WING USB desconectado: aguardando", _controller.Status.OutputLine);
        Assert.Empty(_audio.Writes("default"));
        Assert.Empty(_audio.Writes("master"));

        _audio.DefaultConsole = _audio.DefaultMultimedia = _audio.DefaultCommunications = SpeakersId;
        _audio.Add(new AudioDevice(WingId, FakeAudioSystem.WingName));
        Wait(AudioController.Debounce);

        Assert.Equal([$"default:{WingId}"], _audio.Writes("default"));
        Assert.Equal([$"master:{WingId}:80:False"], _audio.Writes("master"));
        Assert.Contains("Saida de audio: WING USB voltou, selecionada", LogLines);
        Assert.Equal("Sa\U000000EDda atual: WING USB", _controller.Status.OutputLine);
    }

    [Fact]
    public void The_same_device_on_another_usb_port_is_found_by_name_and_its_new_id_is_saved()
    {
        Change(a => a with { OutputDeviceId = "{old-id}", OutputDeviceName = "Alto-falantes (WING USB)" });
        _audio.Devices.Clear();
        _audio.Devices.Add(new AudioDevice("{new-id}", "Alto-falantes (2- WING USB)"));
        _audio.Devices.Add(new AudioDevice(SpeakersId, FakeAudioSystem.SpeakersName));

        Boot();

        Assert.Equal("{new-id}", _audio.DefaultConsole);
        Assert.Equal("{new-id}", _settings.Current.Audio.OutputDeviceId);
    }

    [Fact]
    public void A_device_chosen_by_the_user_is_kept_and_a_lookalike_is_not_picked_instead_when_it_is_absent()
    {
        Change(a => a with { OutputDeviceId = SpeakersId, OutputDeviceName = FakeAudioSystem.SpeakersName });
        _audio.Remove(SpeakersId);

        Boot();

        Assert.Empty(_audio.Writes("default")); // the WING is there, but the chosen output is the speakers: never force another
        Assert.Equal(SpeakersId, _settings.Current.Audio.OutputDeviceId);
    }

    // ---- choosing the output ----

    [Fact]
    public void With_no_choice_the_first_output_with_WING_in_the_name_is_picked_and_saved_whatever_the_case()
    {
        _audio.Devices.Clear();
        _audio.Devices.Add(new AudioDevice(SpeakersId, FakeAudioSystem.SpeakersName));
        _audio.Devices.Add(new AudioDevice("{wing}", "Line (behringer wing usb)"));
        _audio.Devices.Add(new AudioDevice("{wing2}", "Line (WING 2)"));

        Boot();

        Assert.Equal("{wing}", _settings.Current.Audio.OutputDeviceId);
        Assert.Equal("Line (behringer wing usb)", _settings.Current.Audio.OutputDeviceName);
    }

    [Fact]
    public void With_no_WING_yet_nothing_is_chosen_and_it_keeps_trying_when_devices_change()
    {
        _audio.Remove(WingId);
        Boot();
        Assert.Null(_settings.Current.Audio.OutputDeviceId);
        Assert.Empty(_audio.Writes("default"));

        _audio.Add(new AudioDevice(WingId, FakeAudioSystem.WingName));
        Wait(AudioController.Debounce);

        Assert.Equal(WingId, _settings.Current.Audio.OutputDeviceId);
        Assert.Equal(WingId, _audio.DefaultConsole);
    }

    // ---- status ----

    [Fact]
    public void The_status_lists_the_programs_still_waiting_to_open_and_what_was_applied()
    {
        Boot();

        Assert.Equal(["Holyrics: aguardando abrir", "Spotify: aguardando abrir"], _controller.Status.Waiting);
        Assert.Contains("Volume geral 80%", _controller.Status.AppliedSummary);

        _audio.AddSession(WingId, "s-spot", "Spotify");
        Wait(AudioController.Debounce);

        Assert.Equal(["Holyrics: aguardando abrir"], _controller.Status.Waiting);
        Assert.Equal("Volume geral 80%, Spotify 100%", _controller.Status.AppliedSummary);
        Assert.StartsWith("Aplicado ", _controller.Status.AppliedLine, StringComparison.Ordinal);
        Assert.EndsWith("Holyrics: aguardando abrir", _controller.Status.AppliedLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_waits_after_the_30_minutes()
    {
        Boot();
        Wait(AudioController.SessionWindow + TimeSpan.FromMinutes(1));
        _audio.Add(new AudioDevice("{x}", "Fones (USB)")); // any event refreshes the status
        Wait(AudioController.Debounce);

        Assert.Empty(_controller.Status.Waiting);
    }

    [Fact]
    public void The_status_event_fires_when_something_changes_and_lists_the_devices()
    {
        var seen = new List<AudioStatus>();
        _controller.StatusChanged += seen.Add;

        Boot();

        Assert.NotEmpty(seen);
        Assert.Equal([FakeAudioSystem.WingName, FakeAudioSystem.SpeakersName], seen[^1].Devices.Select(d => d.Name));
        Assert.Equal("WING USB", seen[^1].CurrentOutputName);
    }

    // ---- safety ----

    [Fact]
    public void A_broken_audio_service_never_throws_and_the_status_still_works()
    {
        _audio.ThrowOnRead = true;

        _controller.Start();
        Wait(AudioController.StartDelay + AudioController.Debounce);
        _controller.ApplyNow();

        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Empty(_audio.Writes("master"));
        Assert.NotNull(_controller.Status);
    }

    [Fact]
    public void A_refused_volume_is_retried_on_the_next_look_and_logged_without_throwing()
    {
        _audio.FailSetVolume = true;
        Boot();
        Assert.Single(_audio.Writes("master"));

        _audio.FailSetVolume = false;
        _audio.Add(new AudioDevice("{x}", "Fones (USB)"));
        Wait(AudioController.Debounce);

        Assert.Equal(2, _audio.Writes("master").Count());
    }

    [Fact]
    public void Log_lines_only_hold_device_names_process_names_and_percentages()
    {
        _audio.AddSession(WingId, "SECRET-KEY-123", "Spotify", raise: false);
        Boot();
        _controller.ApplyNow();

        Assert.NotEmpty(LogLines);
        Assert.All(LogLines, line =>
        {
            Assert.DoesNotContain("SECRET-KEY-123", line, StringComparison.Ordinal);
            Assert.DoesNotContain(WingId, line, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Sessions_are_only_watched_on_the_output_in_use()
    {
        Boot();
        Assert.Equal(WingId, _audio.Watching);

        _audio.Remove(WingId);
        Wait(AudioController.Debounce);
        Assert.Null(_audio.Watching);
    }

    [Fact]
    public void Disposing_stops_everything_and_closes_the_audio()
    {
        Boot();
        _controller.Dispose();
        _audio.Calls.Clear();

        _audio.WindowsSwitchesTo(SpeakersId);
        Wait(TimeSpan.FromMinutes(5));

        Assert.Empty(_audio.Writes("default"));
    }

    [Fact]
    public void The_tray_toggle_flips_the_lock_in_the_settings()
    {
        Assert.True(_settings.Current.Audio.LockOutput);

        _controller.ToggleLock();
        Assert.False(_settings.Current.Audio.LockOutput);

        _controller.ToggleLock();
        Assert.True(_settings.Current.Audio.LockOutput);
    }
}

using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Tests.Settings;

// The audio section of settings.json: optional, so every older file keeps loading, and every value held in range.
public sealed class AudioSettingsTests
{
    private static void AssertDefaultApps(IReadOnlyList<AudioAppEntry> apps)
    {
        Assert.Equal(["Holyrics", "Spotify"], apps.Select(a => a.Name));
        Assert.Equal(["Holyrics", "javaw"], apps[0].ProcessNames);
        Assert.Equal(["Spotify"], apps[1].ProcessNames);
        Assert.All(apps, a =>
        {
            Assert.Equal(100, a.Volume);
            Assert.False(a.Muted);
        });
    }

    [Fact]
    public void The_defaults_are_the_ones_of_the_church()
    {
        var a = new AppSettings().Audio;

        Assert.True(a.Enabled);
        Assert.Equal(80, a.MasterVolume);
        Assert.False(a.MasterMuted);
        Assert.Equal(0, a.SystemSoundsVolume);
        Assert.False(a.SystemSoundsMuted);
        Assert.True(a.LockOutput);
        Assert.Null(a.OutputDeviceId);
        Assert.Null(a.OutputDeviceName);
        AssertDefaultApps(a.Apps);
    }

    [Fact]
    public void A_settings_file_from_before_the_sound_tab_loads_with_the_defaults()
    {
        const string Old = """
            {
              "version": 1,
              "bar": { "height": 60 },
              "mouseLock": { "enabled": true }
            }
            """;

        var (settings, corrupt) = SettingsJson.Load(Old);

        Assert.False(corrupt);
        Assert.True(settings.Audio.Enabled);
        Assert.Equal(80, settings.Audio.MasterVolume);
        Assert.True(settings.Audio.LockOutput);
        AssertDefaultApps(settings.Audio.Apps);
        Assert.Equal(60, settings.Bar.Height);
        Assert.True(settings.MouseLock.Enabled);
    }

    [Fact]
    public void A_partial_section_fills_the_missing_fields_with_the_defaults()
    {
        var (settings, _) = SettingsJson.Load("""{ "audio": { "masterVolume": 65, "lockOutput": false } }""");

        Assert.Equal(65, settings.Audio.MasterVolume);
        Assert.False(settings.Audio.LockOutput);
        Assert.True(settings.Audio.Enabled);
        AssertDefaultApps(settings.Audio.Apps);
    }

    [Fact]
    public void Volumes_are_held_between_0_and_100()
    {
        var (settings, _) = SettingsJson.Load("""{ "audio": { "masterVolume": 120, "systemSoundsVolume": -5, "apps": [ { "name": "A", "processNames": ["a"], "volume": 999 }, { "name": "B", "processNames": ["b"], "volume": -1 } ] } }""");

        Assert.Equal(100, settings.Audio.MasterVolume);
        Assert.Equal(0, settings.Audio.SystemSoundsVolume);
        Assert.Equal([100, 0], settings.Audio.Apps.Select(a => a.Volume));
    }

    [Fact]
    public void Values_of_the_wrong_type_or_a_section_that_is_not_an_object_give_the_defaults()
    {
        var (wrong, corrupt) = SettingsJson.Load("""{ "audio": { "enabled": "yes", "masterVolume": "loud", "masterMuted": 1, "apps": 7, "lockOutput": null } }""");
        var (notObject, corrupt2) = SettingsJson.Load("""{ "audio": [1, 2] }""");

        Assert.False(corrupt);
        Assert.False(corrupt2);
        foreach (var audio in new[] { wrong.Audio, notObject.Audio })
        {
            Assert.True(audio.Enabled);
            Assert.Equal(80, audio.MasterVolume);
            Assert.False(audio.MasterMuted);
            Assert.True(audio.LockOutput);
            AssertDefaultApps(audio.Apps);
        }
    }

    [Fact]
    public void Entries_without_a_name_or_a_process_are_dropped_and_an_empty_list_gives_the_defaults()
    {
        var (mixed, _) = SettingsJson.Load("""{ "audio": { "apps": [ { "name": " ", "processNames": ["x"] }, { "name": "NoProcess", "processNames": [] }, { "name": "Ok", "processNames": [" ok ", "OK", "", 5] }, "text", { "name": "ok", "processNames": ["dup"] } ] } }""");
        var (empty, _) = SettingsJson.Load("""{ "audio": { "apps": [] } }""");
        var (junk, _) = SettingsJson.Load("""{ "audio": { "apps": [ 1, "a", { } ] } }""");

        var only = Assert.Single(mixed.Audio.Apps);
        Assert.Equal("Ok", only.Name);
        Assert.Equal(["ok"], only.ProcessNames);
        Assert.Equal(100, only.Volume);
        AssertDefaultApps(empty.Audio.Apps);
        AssertDefaultApps(junk.Audio.Apps);
    }

    [Fact]
    public void Everything_survives_a_save_and_a_load()
    {
        var original = new AppSettings
        {
            Audio = new AudioSettings
            {
                Enabled = false,
                MasterVolume = 33,
                MasterMuted = true,
                SystemSoundsVolume = 7,
                SystemSoundsMuted = true,
                Apps = [new AudioAppEntry("Mixer", ["mixer", "mixer.exe"], 45, true)],
                LockOutput = false,
                OutputDeviceId = "{0.0.0.00000000}.{abc}",
                OutputDeviceName = "Alto-falantes (WING USB)",
            },
        };

        var back = SettingsJson.Load(SettingsJson.Save(original)).Settings.Audio;

        Assert.False(back.Enabled);
        Assert.Equal(33, back.MasterVolume);
        Assert.True(back.MasterMuted);
        Assert.Equal(7, back.SystemSoundsVolume);
        Assert.True(back.SystemSoundsMuted);
        Assert.False(back.LockOutput);
        Assert.Equal("{0.0.0.00000000}.{abc}", back.OutputDeviceId);
        Assert.Equal("Alto-falantes (WING USB)", back.OutputDeviceName);
        var app = Assert.Single(back.Apps);
        Assert.Equal(new AudioAppEntry("Mixer", ["mixer", "mixer.exe"], 45, true).Name, app.Name);
        Assert.Equal(["mixer", "mixer.exe"], app.ProcessNames);
        Assert.Equal(45, app.Volume);
        Assert.True(app.Muted);
    }

    [Fact]
    public void The_saved_text_has_the_audio_section_and_normalizing_twice_changes_nothing()
    {
        var text = SettingsJson.Save(new AppSettings());
        var once = SettingsJson.Normalize(new AppSettings { Audio = new AudioSettings { MasterVolume = 500, OutputDeviceId = "  ", OutputDeviceName = " X " } });
        var twice = SettingsJson.Normalize(once);

        Assert.Contains("\"audio\"", text, StringComparison.Ordinal);
        Assert.Equal(100, once.Audio.MasterVolume);
        Assert.Null(once.Audio.OutputDeviceId);
        Assert.Equal("X", once.Audio.OutputDeviceName);
        Assert.Equal(SettingsJson.Save(once), SettingsJson.Save(twice));
    }
}

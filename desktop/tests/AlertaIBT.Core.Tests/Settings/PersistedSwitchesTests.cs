using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Tests.Settings;

// Batch 39: EVERY on/off switch that controls a function is saved and comes back as the owner left it. The factory default only applies when
// there is no saved value. This is the list (docs/INTERRUPTORES-SALVOS.md has the same table); a new switch is a new line here.
public sealed class PersistedSwitchesTests
{
    private sealed record Switch(string Name, Func<AppSettings, bool> Get, Func<AppSettings, bool, AppSettings> Set, bool FactoryDefault);

    private static readonly Switch[] All =
    [
        new("System.AlertsEnabled", s => s.System.AlertsEnabled, (s, v) => s with { System = s.System with { AlertsEnabled = v } }, true),
        new("System.ClearHistoryWeekly", s => s.System.ClearHistoryWeekly, (s, v) => s with { System = s.System with { ClearHistoryWeekly = v } }, true),
        new("System.AutoUpdate", s => s.System.AutoUpdate, (s, v) => s with { System = s.System with { AutoUpdate = v } }, true),
        new("System.AppWindowsInScreenshots", s => s.System.AppWindowsInScreenshots, (s, v) => s with { System = s.System with { AppWindowsInScreenshots = v } }, true),
        new("Site.Enabled", s => s.Site.Enabled, (s, v) => s with { Site = s.Site with { Enabled = v } }, false),
        new("Correction.Enabled", s => s.Correction.Enabled, (s, v) => s with { Correction = s.Correction with { Enabled = v } }, true),
        new("MouseLock.Enabled", s => s.MouseLock.Enabled, (s, v) => s with { MouseLock = s.MouseLock with { Enabled = v } }, false),
        new("MouseLock.TripleCtrl", s => s.MouseLock.TripleCtrl, (s, v) => s with { MouseLock = s.MouseLock with { TripleCtrl = v } }, true),
        new("Audio.Enabled", s => s.Audio.Enabled, (s, v) => s with { Audio = s.Audio with { Enabled = v } }, true),
        new("Audio.MasterMuted", s => s.Audio.MasterMuted, (s, v) => s with { Audio = s.Audio with { MasterMuted = v } }, false),
        new("Audio.SystemSoundsMuted", s => s.Audio.SystemSoundsMuted, (s, v) => s with { Audio = s.Audio with { SystemSoundsMuted = v } }, false),
        new("Audio.LockOutput", s => s.Audio.LockOutput, (s, v) => s with { Audio = s.Audio with { LockOutput = v } }, true),
        new("Audio.HolyricsRoute", s => s.Audio.HolyricsRoute, (s, v) => s with { Audio = s.Audio with { HolyricsRoute = v } }, true),
        new("Audio.Apps[0].Muted", s => s.Audio.Apps[0].Muted, (s, v) => s with { Audio = s.Audio with { Apps = [s.Audio.Apps[0] with { Muted = v }, .. s.Audio.Apps.Skip(1)] } }, false),
        new("Player.Enabled", s => s.Player.Enabled, (s, v) => s with { Player = s.Player with { Enabled = v } }, true),
        new("Player.StartMuted", s => s.Player.StartMuted, (s, v) => s with { Player = s.Player with { StartMuted = v } }, false),
        new("Player.Shuffle", s => s.Player.Shuffle, (s, v) => s with { Player = s.Player with { Shuffle = v } }, true),
        new("Player.MediaKeys", s => s.Player.MediaKeys, (s, v) => s with { Player = s.Player with { MediaKeys = v } }, true),
        new("Player.NormalizeEnabled", s => s.Player.NormalizeEnabled, (s, v) => s with { Player = s.Player with { NormalizeEnabled = v } }, true),
        new("Player.WriteLoudnessTags", s => s.Player.WriteLoudnessTags, (s, v) => s with { Player = s.Player with { WriteLoudnessTags = v } }, false),
        new("Player.AlarmsEnabled", s => s.Player.AlarmsEnabled, (s, v) => s with { Player = s.Player with { AlarmsEnabled = v } }, true),
        new("Player.Alarms[0].Enabled", s => s.Player.Alarms[0].Enabled, (s, v) => s with { Player = s.Player with { Alarms = [s.Player.Alarms[0] with { Enabled = v }, .. s.Player.Alarms.Skip(1)] } }, true),
        new("Ndi.LayerOn", s => s.Ndi.LayerOn, (s, v) => s with { Ndi = s.Ndi with { LayerOn = v } }, false),
        new("Ndi.TestPattern", s => s.Ndi.TestPattern, (s, v) => s with { Ndi = s.Ndi with { TestPattern = v } }, false),
        new("Ndi.HideFromCapture", s => s.Ndi.HideFromCapture, (s, v) => s with { Ndi = s.Ndi with { HideFromCapture = v } }, false),
        new("Ndi.ShortcutsEnabled", s => s.Ndi.ShortcutsEnabled, (s, v) => s with { Ndi = s.Ndi with { ShortcutsEnabled = v } }, true),
        new("Ndi.MonitorLocked", s => s.Ndi.MonitorLocked, (s, v) => s with { Ndi = s.Ndi with { MonitorLocked = v } }, true),
        new("Control.Enabled", s => s.Control.Enabled, (s, v) => s with { Control = s.Control with { Enabled = v } }, false),
        new("Control.UniverseOffset", s => s.Control.UniverseOffset, (s, v) => s with { Control = s.Control with { UniverseOffset = v } }, true),
        new("Control.Rows[0].Enabled", s => s.Control.Rows[0].Enabled, (s, v) => s with { Control = s.Control with { Rows = [s.Control.Rows[0] with { Enabled = v }, .. s.Control.Rows.Skip(1)] } }, true),
        new("Holyrics.ArrowsEnabled", s => s.Holyrics.ArrowsEnabled, (s, v) => s with { Holyrics = s.Holyrics with { ArrowsEnabled = v } }, true),
        new("Holyrics.PresentationMode", s => s.Holyrics.PresentationMode, (s, v) => s with { Holyrics = s.Holyrics with { PresentationMode = v } }, false),
        new("Holyrics.CountdownStopEnabled", s => s.Holyrics.CountdownStopEnabled, (s, v) => s with { Holyrics = s.Holyrics with { CountdownStopEnabled = v } }, true),
        new("Backup.AutoEnabled", s => s.Backup.AutoEnabled, (s, v) => s with { Backup = s.Backup with { AutoEnabled = v } }, false),
    ];

    public static TheoryData<string> Names() => new(All.Select(s => s.Name));

    private static Switch Find(string name) => All.Single(s => s.Name == name);

    [Theory]
    [MemberData(nameof(Names))]
    public void The_factory_default_is_what_a_first_install_gets(string name)
    {
        var sw = Find(name);

        Assert.Equal(sw.FactoryDefault, sw.Get(new AppSettings()));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void The_last_position_survives_a_save_and_a_load_and_touches_no_other_switch(string name)
    {
        var sw = Find(name);
        var flipped = sw.Set(new AppSettings(), !sw.FactoryDefault);

        var (loaded, corrupt) = SettingsJson.Load(SettingsJson.Save(flipped));

        Assert.False(corrupt);
        Assert.Equal(!sw.FactoryDefault, sw.Get(loaded));
        foreach (var other in All.Where(o => o.Name != name))
        {
            Assert.True(other.FactoryDefault == other.Get(loaded), $"{other.Name} changed when only {name} was flipped");
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void A_file_written_before_the_key_existed_loads_the_factory_default_and_flips_nothing(string name)
    {
        var sw = Find(name);

        // A file from before batch 39: no key of the three new switches at all, every other switch set to the opposite of its default.
        var everything = All.Aggregate(new AppSettings(), (settings, one) => one.Set(settings, !one.FactoryDefault));
        var json = SettingsJson.Save(everything);
        foreach (var key in new[] { "layerOn", "testPattern", "presentationMode", "countdownStopEnabled" })
        {
            json = System.Text.RegularExpressions.Regex.Replace(json, "\\s*\"" + key + "\"\\s*:\\s*(true|false),?", string.Empty);
        }

        var (loaded, corrupt) = SettingsJson.Load(json);

        Assert.False(corrupt);
        var isNew = name is "Holyrics.CountdownStopEnabled" or "Ndi.LayerOn" or "Ndi.TestPattern" or "Holyrics.PresentationMode";
        Assert.Equal(isNew ? sw.FactoryDefault : !sw.FactoryDefault, sw.Get(loaded));
    }

    [Fact]
    public void A_damaged_value_of_a_new_switch_falls_back_to_its_default_without_corrupting_the_file()
    {
        var (settings, corrupt) = SettingsJson.Load("""{"ndi":{"layerOn":1,"testPattern":null},"holyrics":{"presentationMode":"on"}}""");

        Assert.False(corrupt);
        Assert.False(settings.Ndi.LayerOn);
        Assert.False(settings.Ndi.TestPattern);
        Assert.False(settings.Holyrics.PresentationMode);
    }

    [Fact]
    public void The_bar_pause_is_the_one_switch_that_is_never_saved_an_old_barPaused_key_is_ignored_and_dropped()
    {
        // "Pausar barra" is the exception to the rule: the app always starts with the bar running, so the alerts work after the PC is turned on again.
        var (loaded, corrupt) = SettingsJson.Load("""{"system":{"barPaused":true,"alertsEnabled":false}}""");

        Assert.False(corrupt);
        Assert.False(loaded.System.AlertsEnabled); // the rest of the old file still counts
        Assert.DoesNotContain("barPaused", SettingsJson.Save(loaded));
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("offline")]
    public void The_correction_mode_gemini_or_offline_survives(string mode)
    {
        var original = new AppSettings { Correction = new CorrectionSettings { Mode = mode } };

        var (loaded, _) = SettingsJson.Load(SettingsJson.Save(original));

        Assert.Equal(mode, loaded.Correction.Mode);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("one")]
    [InlineData("off")]
    public void The_repeat_mode_survives(string mode)
    {
        var original = new AppSettings { Player = new PlayerSettings { Repeat = mode } };

        var (loaded, _) = SettingsJson.Load(SettingsJson.Save(original));

        Assert.Equal(mode, loaded.Player.Repeat);
    }

    [Fact]
    public void The_backup_switch_keeps_its_stored_password_and_folder()
    {
        var original = new AppSettings { Backup = new BackupSettings { AutoEnabled = true, Folder = @"C:\copias", PasswordProtected = "AAECAw==" } };

        var (loaded, _) = SettingsJson.Load(SettingsJson.Save(original));

        Assert.True(loaded.Backup.AutoEnabled);
        Assert.Equal(@"C:\copias", loaded.Backup.Folder);
        Assert.Equal("AAECAw==", loaded.Backup.PasswordProtected);
    }
}

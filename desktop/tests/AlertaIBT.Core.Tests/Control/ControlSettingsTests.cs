using AlertaIBT.Core.Control;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Tests.Control;

// The "control" section of settings.json: optional (an older file loads with the defaults), held in range, and the owner's 21 rows.
public sealed class ControlSettingsTests
{
    [Fact]
    public void The_defaults_are_off_universe_100_with_the_offset_on_and_no_source_filter()
    {
        var c = new AppSettings().Control;

        Assert.False(c.Enabled);
        Assert.Equal(100, c.Universe);
        Assert.True(c.UniverseOffset);
        Assert.Equal(string.Empty, c.SourceIp);
        Assert.Equal(99, c.ArtNetUniverse); // shown 100 on the MA2 = Art-Net 99 on the wire
    }

    [Fact]
    public void Without_the_offset_the_universe_is_the_art_net_one()
    {
        var c = new ControlSettings { Universe = 100, UniverseOffset = false };

        Assert.Equal(100, c.ArtNetUniverse);
        Assert.Equal(0, c.MinUniverse);
    }

    [Fact]
    public void The_default_map_is_exactly_the_21_rows_of_the_owner()
    {
        var rows = new ControlSettings().Rows;

        Assert.Equal(21, rows.Count);
        Assert.Equal(Enumerable.Range(1, 21), rows.Select(r => r.Channel));

        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(new ControlRow(i + 1, ControlAction.SelectCamera, ControlMode.Select, ControlTrigger.AnyChange, i + 1), rows[i]);
        }

        Assert.Equal(new ControlRow(7, ControlAction.CameraOnTelao, ControlMode.State), rows[6]);
        Assert.Equal(ControlAction.Take, rows[7].Action);
        Assert.Equal(ControlAction.PlayPause, rows[8].Action);
        Assert.Equal(ControlAction.Stop, rows[9].Action);
        Assert.Equal(ControlAction.Next, rows[10].Action);
        Assert.Equal(ControlAction.Previous, rows[11].Action);
        Assert.All(rows.Skip(7).Take(5), r => Assert.Equal(ControlMode.Button, r.Mode));
        Assert.All(rows.Skip(7).Take(5), r => Assert.Equal(ControlTrigger.AnyChange, r.Trigger));
        Assert.All(rows.Skip(7).Take(5), r => Assert.True(r.Enabled));

        // 13..16 are the Holyrics ones (slides, video, logo): active since the Holyrics batch.
        Assert.Equal([ControlAction.SlideNext, ControlAction.SlidePrevious, ControlAction.VideoPlayPause, ControlAction.LogoToggle], rows.Skip(12).Take(4).Select(r => r.Action));
        Assert.All(rows.Skip(12).Take(4), r => Assert.True(r.Enabled));

        Assert.Equal(
            [ControlAction.PreviewVertical, ControlAction.ProgramVertical, ControlAction.VolumeWindows, ControlAction.VolumeHolyrics, ControlAction.VolumePlayer],
            rows.Skip(16).Select(r => r.Action));
        Assert.All(rows.Skip(16), r =>
        {
            Assert.Equal(ControlMode.Fader, r.Mode);
            Assert.True(r.Pickup);
            Assert.True(r.Enabled);
        });
    }

    [Fact]
    public void An_older_file_without_the_section_loads_with_the_defaults()
    {
        var (settings, corrupt) = SettingsJson.Load("""{ "version": 1, "bar": { "height": 60 } }""");

        Assert.False(corrupt);
        Assert.Equal(new ControlSettings(), settings.Control);
    }

    [Fact]
    public void The_section_survives_a_save_and_a_load()
    {
        var original = new AppSettings
        {
            Control = new ControlSettings
            {
                Enabled = true,
                Universe = 7,
                UniverseOffset = false,
                SourceIp = "2.0.0.2",
                Rows = [new ControlRow(30, ControlAction.Take, ControlMode.Button, ControlTrigger.RisingOnly), new ControlRow(31, ControlAction.VolumePlayer, ControlMode.Fader, Pickup: false)],
            },
        };

        var (loaded, _) = SettingsJson.Load(SettingsJson.Save(original));

        Assert.Equal(original.Control, loaded.Control);
    }

    [Fact]
    public void Values_out_of_range_are_held_in_range_and_garbage_rows_are_dropped()
    {
        const string Json = """
            {
              "control": {
                "enabled": true, "universe": 99999, "universeOffset": true, "sourceIp": "not an ip",
                "rows": [
                  { "channel": 900, "action": "take", "mode": "fader" },
                  { "channel": 0, "action": "selectCamera", "mode": "select", "parameter": 99 },
                  { "channel": 5, "action": "doesNotExist" },
                  "junk",
                  { "channel": 9, "action": "slideNext", "enabled": true }
                ]
              }
            }
            """;

        var (settings, _) = SettingsJson.Load(Json);
        var c = settings.Control;

        Assert.True(c.Enabled);
        Assert.Equal(32768, c.Universe);
        Assert.Equal(string.Empty, c.SourceIp);
        Assert.Equal(3, c.Rows.Count);
        Assert.Equal(new ControlRow(512, ControlAction.Take, ControlMode.Button), c.Rows[0]); // the fader mode is not one a Take accepts
        Assert.Equal(new ControlRow(1, ControlAction.SelectCamera, ControlMode.Select, ControlTrigger.AnyChange, 6), c.Rows[1]);
        Assert.True(c.Rows[2].Enabled); // a Holyrics row is a real action now
    }

    [Fact]
    public void An_empty_map_stays_empty_and_a_missing_one_gets_the_defaults()
    {
        Assert.Empty(SettingsJson.Load("""{ "control": { "rows": [] } }""").Settings.Control.Rows);
        Assert.Equal(21, SettingsJson.Load("""{ "control": { "enabled": true } }""").Settings.Control.Rows.Count);
    }

    [Fact]
    public void The_text_forms_round_trip()
    {
        foreach (var info in ControlCatalog.All)
        {
            Assert.Equal(info.Action, ControlCatalog.ParseAction(ControlCatalog.ToText(info.Action)));
        }

        Assert.Null(ControlCatalog.ParseAction("nope"));
        Assert.Equal(ControlMode.State, ControlCatalog.ParseMode("state"));
        Assert.Equal(ControlTrigger.RisingOnly, ControlCatalog.ParseTrigger("risingOnly"));
    }

    [Fact]
    public void The_control_section_is_not_part_of_a_backup()
    {
        var withControl = new AppSettings { Control = new ControlSettings { Enabled = true, Universe = 3 } };
        var plain = new AppSettings();

        var a = AlertaIBT.Core.Backup.SettingsBackup.Capture(withControl, new Settings.FakeProtector(), DateTimeOffset.UnixEpoch);
        var b = AlertaIBT.Core.Backup.SettingsBackup.Capture(plain, new Settings.FakeProtector(), DateTimeOffset.UnixEpoch);

        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(a), System.Text.Json.JsonSerializer.Serialize(b));
    }
}

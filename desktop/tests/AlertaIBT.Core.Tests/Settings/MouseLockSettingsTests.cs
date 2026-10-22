using System.Text.Json;
using AlertaIBT.Core.Input;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Tests.Settings;

// The mouse-lock section of settings.json: optional, so every older file keeps loading.
public sealed class MouseLockSettingsTests
{
    [Fact]
    public void The_defaults_are_off_Ctrl_Alt_M_and_Ctrl_x3_on()
    {
        var d = new AppSettings().MouseLock;

        Assert.False(d.Enabled);
        Assert.Equal("Ctrl+Alt+M", d.Hotkey);
        Assert.True(d.TripleCtrl);
    }

    [Fact]
    public void The_default_shortcut_passes_the_same_validation_as_the_panel_one()
    {
        Assert.True(HotkeyGesture.TryParse(MouseLockSettings.DefaultHotkey, out var gesture));
        Assert.Equal(HotkeyProblem.None, HotkeyGesture.Validate(gesture.Modifiers, gesture.VirtualKey));
        Assert.Equal("Ctrl+Alt+M", gesture.ToString());
    }

    [Fact]
    public void A_settings_file_from_before_the_mouse_lock_loads_with_the_defaults()
    {
        const string Old = """
            {
              "version": 1,
              "bar": { "height": 60 },
              "system": { "hotkey": "Ctrl+Alt+Space", "autoUpdate": false }
            }
            """;

        var (settings, corrupt) = SettingsJson.Load(Old);

        Assert.False(corrupt);
        Assert.Equal(new MouseLockSettings(), settings.MouseLock);
        Assert.Equal(60, settings.Bar.Height);
        Assert.False(settings.System.AutoUpdate);
    }

    [Fact]
    public void A_partial_section_fills_the_missing_fields_with_the_defaults()
    {
        var (settings, _) = SettingsJson.Load("""{ "mouseLock": { "enabled": true } }""");

        Assert.True(settings.MouseLock.Enabled);
        Assert.Equal("Ctrl+Alt+M", settings.MouseLock.Hotkey);
        Assert.True(settings.MouseLock.TripleCtrl);
    }

    [Fact]
    public void Values_of_the_wrong_type_or_a_section_that_is_not_an_object_give_the_defaults()
    {
        var (wrongTypes, corrupt) = SettingsJson.Load("""{ "mouseLock": { "enabled": "yes", "hotkey": 7, "tripleCtrl": null } }""");
        var (notAnObject, corruptToo) = SettingsJson.Load("""{ "mouseLock": 5 }""");

        Assert.False(corrupt);
        Assert.False(corruptToo);
        Assert.Equal(new MouseLockSettings(), wrongTypes.MouseLock);
        Assert.Equal(new MouseLockSettings(), notAnObject.MouseLock);
    }

    [Fact]
    public void A_blank_shortcut_becomes_the_default_one()
    {
        var (settings, _) = SettingsJson.Load("""{ "mouseLock": { "hotkey": "   " } }""");

        Assert.Equal("Ctrl+Alt+M", settings.MouseLock.Hotkey);
    }

    [Fact]
    public void An_unusable_saved_shortcut_is_kept_as_text_for_the_app_to_replace_and_log()
    {
        var (settings, _) = SettingsJson.Load("""{ "mouseLock": { "hotkey": "Ctrl+V" } }""");

        Assert.Equal("Ctrl+V", settings.MouseLock.Hotkey); // the registration step falls back to the default, with a log line
        Assert.False(HotkeyGesture.TryParse(settings.MouseLock.Hotkey, out _));
    }

    [Fact]
    public void The_section_round_trips_through_save_and_load()
    {
        var original = new AppSettings
        {
            MouseLock = new MouseLockSettings { Enabled = true, Hotkey = "Ctrl+Shift+F9", TripleCtrl = false },
        };

        var (loaded, _) = SettingsJson.Load(SettingsJson.Save(original));

        Assert.Equal(original.MouseLock, loaded.MouseLock);
    }

    [Fact]
    public void Save_writes_a_mouseLock_section_with_the_three_fields()
    {
        using var document = JsonDocument.Parse(SettingsJson.Save(new AppSettings()));

        var section = document.RootElement.GetProperty("mouseLock");
        Assert.False(section.GetProperty("enabled").GetBoolean());
        Assert.Equal("Ctrl+Alt+M", section.GetProperty("hotkey").GetString());
        Assert.True(section.GetProperty("tripleCtrl").GetBoolean());
    }

    [Fact]
    public void Normalizing_twice_changes_nothing_more()
    {
        var once = SettingsJson.Normalize(new AppSettings { MouseLock = new MouseLockSettings { Hotkey = "  Ctrl+Alt+F8  " } });

        Assert.Equal("Ctrl+Alt+F8", once.MouseLock.Hotkey);
        Assert.Equal(once, SettingsJson.Normalize(once));
    }
}

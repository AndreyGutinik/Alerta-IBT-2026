using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Tests.Settings;

// "Janelas do app aparecem em prints" (Configuracoes > Sistema): on by default and for a settings.json from before it.
public sealed class AppWindowsInScreenshotsSettingTests
{
    [Fact]
    public void The_default_is_on()
    {
        Assert.True(new AppSettings().System.AppWindowsInScreenshots);
    }

    [Fact]
    public void An_old_settings_file_without_the_field_loads_on_and_is_not_corrupt()
    {
        var (settings, corrupt) = SettingsJson.Load("""{"version":1,"system":{"hotkey":"Ctrl+Alt+F1"}}""");

        Assert.False(corrupt);
        Assert.True(settings.System.AppWindowsInScreenshots);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_value_survives_a_save_and_a_load(bool value)
    {
        var original = new AppSettings { System = new SystemSettings { AppWindowsInScreenshots = value } };

        var (loaded, corrupt) = SettingsJson.Load(SettingsJson.Save(original));

        Assert.False(corrupt);
        Assert.Equal(value, loaded.System.AppWindowsInScreenshots);
    }

    [Fact]
    public void A_damaged_value_falls_back_to_on()
    {
        var (settings, corrupt) = SettingsJson.Load("""{"system":{"appWindowsInScreenshots":"nao"}}""");

        Assert.False(corrupt);
        Assert.True(settings.System.AppWindowsInScreenshots);
    }
}

using AlertaIBT.App.Hosting;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.Hosting;

public sealed class StartupArgumentsTests
{
    [Fact]
    public void No_arguments_means_a_manual_start_on_the_real_led()
    {
        Assert.Equal(new StartupArguments(false, false), StartupArguments.Parse([]));
        Assert.Equal(new StartupArguments(false, false), StartupArguments.Parse(null));
    }

    [Fact]
    public void The_run_entry_switch_starts_in_the_tray()
    {
        Assert.Equal(new StartupArguments(true, false), StartupArguments.Parse(["--tray"]));
    }

    [Fact]
    public void Test_led_and_tray_combine_and_ignore_case_and_unknown_words()
    {
        var parsed = StartupArguments.Parse(["--TEST-LED", "--whatever", "--Tray", "x"]);

        Assert.Equal(new StartupArguments(true, true), parsed);
    }
}

public sealed class AppPathsTests
{
    [Fact]
    public void Files_live_under_the_data_folder_with_the_names_of_the_spec()
    {
        var paths = new AppPaths(Path.Combine("x", "AlertaIBT"));

        Assert.Equal(Path.Combine("x", "AlertaIBT", "settings.json"), paths.SettingsFile);
        Assert.Equal(Path.Combine("x", "AlertaIBT", "queue.json"), paths.QueueFile);
        Assert.Equal(Path.Combine("x", "AlertaIBT", "logs"), paths.LogsFolder);
    }

    [Fact]
    public void The_default_settings_file_is_the_one_core_names()
    {
        // Unless a developer moved the folder with the environment variable.
        if (Environment.GetEnvironmentVariable(AppPaths.DataFolderVariable) is { Length: > 0 })
        {
            return;
        }

        Assert.Equal(SettingsStore.DefaultPath, AppPaths.Default.SettingsFile);
    }
}

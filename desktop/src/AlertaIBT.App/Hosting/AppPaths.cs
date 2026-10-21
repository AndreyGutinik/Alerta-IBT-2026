using System.IO;
namespace AlertaIBT.App.Hosting;

/// <summary>Where the app keeps its files: <c>%AppData%\AlertaIBT</c> with <c>settings.json</c>, <c>queue.json</c> and <c>logs\</c>.</summary>
/// <param name="DataFolder">The folder that holds everything.</param>
public sealed record AppPaths(string DataFolder)
{
    /// <summary>
    /// Environment variable that moves the data folder. Meant for development and tests only (to run a copy of the app
    /// without touching the real settings); the installed app never sets it.
    /// </summary>
    public const string DataFolderVariable = "ALERTAIBT_DATA_DIR";

    /// <summary><c>%AppData%\AlertaIBT</c>, or the folder named by <see cref="DataFolderVariable"/>.</summary>
    public static AppPaths Default { get; } = new(ResolveDataFolder());

    /// <summary><c>settings.json</c>, the same file <see cref="Core.Settings.SettingsStore.DefaultPath"/> names.</summary>
    public string SettingsFile => Path.Combine(DataFolder, "settings.json");

    /// <summary><c>queue.json</c>, the alert queue saved between runs.</summary>
    public string QueueFile => Path.Combine(DataFolder, "queue.json");

    /// <summary>The folder of the daily log files, kept 7 days.</summary>
    public string LogsFolder => Path.Combine(DataFolder, "logs");

    private static string ResolveDataFolder()
    {
        var overridden = Environment.GetEnvironmentVariable(DataFolderVariable);
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlertaIBT")
            : overridden;
    }
}

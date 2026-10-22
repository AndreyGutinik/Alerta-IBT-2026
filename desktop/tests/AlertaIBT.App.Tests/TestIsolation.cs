using System.Runtime.CompilerServices;
using AlertaIBT.App.Hosting;
using AlertaIBT.App.Platform;

namespace AlertaIBT.App.Tests;

/// <summary>
/// Makes the test process unable to touch the copy of the app the church really runs. It runs before the first test (and before
/// any static of the app is read): the single-instance name (mutex and "show your window" event) and the data folder (settings,
/// queue, logs) get names no real program uses, and they are the same for the whole run, so the app's own startup code, should a
/// test reach it, locks, signals and writes only its own.
/// </summary>
internal static class TestIsolation
{
    /// <summary>The single-instance name of this test run.</summary>
    public static string InstanceName { get; } = "AlertaIBT.TestRun." + Guid.NewGuid().ToString("N");

    /// <summary>The data folder of this test run (a temp folder, removed when the process ends).</summary>
    public static string DataFolder { get; } = Path.Combine(Path.GetTempPath(), "alerta-ibt-testrun-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Apply()
    {
        Environment.SetEnvironmentVariable(SingleInstance.InstanceNameVariable, InstanceName);
        Environment.SetEnvironmentVariable(AppPaths.DataFolderVariable, DataFolder);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(DataFolder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: the OS temp cleaner takes what is left.
            }
        };
    }
}

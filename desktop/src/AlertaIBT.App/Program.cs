using AlertaIBT.App.Platform;
using Velopack;

namespace AlertaIBT.App;

/// <summary>The entry point. Velopack must see the command line first: the installer runs the app with hook switches on install, update and uninstall.</summary>
public static class Program
{
    /// <summary>Runs the Velopack hooks, then the WPF application.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build()
            // An update is applied by UpdateService after the single-instance lock is held (a second launch of a running app
            // must only show its window), never by Velopack's own early auto-apply.
            .SetAutoApplyOnStartup(false)
            // Uninstalling takes the app out of "start with Windows"; the settings in %AppData%\AlertaIBT are kept on purpose.
            .OnBeforeUninstallFastCallback(_ => StartupRegistration.Remove(new WindowsRunKeyStore()))
            .Run();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}

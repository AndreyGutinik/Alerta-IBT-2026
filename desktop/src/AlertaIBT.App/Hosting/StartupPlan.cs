namespace AlertaIBT.App.Hosting;

/// <summary>
/// What a launch does with the main window and the tray: the one rule for "how does the app open". The app ALWAYS starts hidden in
/// the notification area (booting the PC, the Start-menu or desktop shortcut, a restart after an update), so it never takes the screen
/// or the focus from Holyrics or OBS. Only two things bring the window up: the very first run after an installation (so the owner sees
/// something), and a second launch while the app is already running (the running copy shows its window).
/// </summary>
/// <param name="ShowWindow">Whether the main window is shown (and, for a second launch, whether the running copy is asked to show it).</param>
/// <param name="ShowTrayBalloon">Whether the tray icon says once that the app is in the tray (a manual launch, so the owner knows it started).</param>
public readonly record struct StartupPlan(bool ShowWindow, bool ShowTrayBalloon)
{
    /// <summary>The title of the tray notification.</summary>
    public const string BalloonTitle = "IBT Controller";

    /// <summary>The text of the tray notification ("IBT Controller esta na bandeja", with the accent in the string below).</summary>
    public const string BalloonMessage = "IBT Controller est\U000000E1 na bandeja";

    /// <summary>Whether this is the first run after an installation: no <c>settings.json</c> yet. Ask before anything creates the file.</summary>
    public static bool IsFirstRun(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return !System.IO.File.Exists(paths.SettingsFile);
    }

    /// <summary>Decides how this launch opens.</summary>
    /// <param name="arguments">The command line. <c>--tray</c> is still accepted (the Windows startup entry passes it) but no longer changes the window: it only keeps the notification quiet.</param>
    /// <param name="firstRun">No <c>settings.json</c> existed before this launch: a fresh installation.</param>
    /// <param name="alreadyRunning">Another copy holds the single-instance lock: this launch only wakes that copy's window and exits.</param>
    /// <param name="restartedByUpdate">The app was just restarted by an automatic update: nobody launched it, so no notification either.</param>
    public static StartupPlan Decide(StartupArguments arguments, bool firstRun, bool alreadyRunning, bool restartedByUpdate = false)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (alreadyRunning || firstRun)
        {
            return new StartupPlan(ShowWindow: true, ShowTrayBalloon: false);
        }

        return new StartupPlan(ShowWindow: false, ShowTrayBalloon: !arguments.Tray && !restartedByUpdate);
    }
}

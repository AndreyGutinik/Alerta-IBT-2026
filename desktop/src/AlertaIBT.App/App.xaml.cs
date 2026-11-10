using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AlertaIBT.App.Hosting;
using AlertaIBT.App.Platform;
using AlertaIBT.App.Tray;
using AlertaIBT.App.Views;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wpf.Ui.Appearance;

namespace AlertaIBT.App;

/// <summary>The WPF application: single instance, host, tray shell. It lives in the tray until the user quits.</summary>
public partial class App : Application
{
    private SingleInstance? _instance;
    private IHost? _host;
    private bool _restartingForUpdate;
    private SessionMarker? _session;
    private UiStallWatchdog? _watchdog;
    private string? _exitReason;
    private SessionStateTracker? _sessionTracker;
    private ShutdownGuard? _shutdownGuard;
    private DispatcherTimer? _episodeTimer;
    private readonly UiFailureEpisodes _failures = new();
    private UiErrorHandler? _uiErrors;

    /// <summary>
    /// Whether <see cref="OnStartup"/> runs the real application (single-instance lock, settings, tray, hotkey, API, window). Always true in
    /// the app. The test host sets it to false before it creates its <see cref="App"/>: WPF posts <c>OnStartup</c> to the dispatcher by itself,
    /// so without this a test run would start, and fight over the PC with, the copy of the app the church really uses.
    /// </summary>
    internal static bool RunsApplication { get; set; } = true;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplicationAccentColorManager.Apply(Color.FromRgb(0x1D, 0xA3, 0x5F), ApplicationTheme.Dark, false, false);
        if (!RunsApplication)
        {
            return;
        }

        var arguments = StartupArguments.Parse(e.Args);

        var paths = AppPaths.Default;

        // No settings file yet: a fresh installation, the one launch that shows the window (read before anything creates the file).
        var firstRun = StartupPlan.IsFirstRun(paths);

        // Just restarted by an update: the old process is leaving (the updater waits for it), so wait for the lock a few seconds
        // instead of taking it for "another copy is running" and exiting. A normal start waits not at all.
        var restartWait = UpdateRestartMarker.ConsumeWait(paths.UpdateRestartFile, DateTimeOffset.UtcNow);
        _instance = SingleInstance.TryAcquire(restartWait);
        if (_instance is null)
        {
            if (StartupPlan.Decide(arguments, firstRun, alreadyRunning: true).ShowWindow)
            {
                SingleInstance.SignalExisting(); // the running copy shows its window
            }

            Shutdown();
            return;
        }

        // How the run before this one ended (a marker file): an end without the end line is a crash, a kill or a power cut.
        _session = new SessionMarker(paths.SessionFile);
        var previousRun = _session.Begin(RunningVersion());

        // Always hidden in the tray, except the first run after an installation (see StartupPlan).
        var plan = StartupPlan.Decide(arguments, firstRun, alreadyRunning: false, restartedByUpdate: restartWait > TimeSpan.Zero);

        ILogger logger = NullLogger<App>.Instance;
        try
        {
            _host = AppHost.Build(paths, arguments);
            logger = _host.Services.GetRequiredService<ILogger<App>>();
            InstallErrorLogging(logger);
            ShutdownSteps.UseLogger(logger);
            ExplainPreviousRun(previousRun, logger);

            logger.LogInformation("Execucao iniciada (versao {Version}, processo {Pid}, .NET {Runtime})", RunningVersion(), Environment.ProcessId, Environment.Version);

            // An update downloaded earlier is installed now, at the start (nothing is serving yet). The single-instance
            // lock is already ours, so a second launch of a running app never gets here.
            if (_host.Services.GetRequiredService<UpdateService>().TryApplyPendingAtStartup(e.Args))
            {
                _restartingForUpdate = true;
                _session.End("reiniciando para instalar a atualizacao");
                _instance.Dispose();
                _instance = null;
                Serilog.Log.CloseAndFlush();
                Shutdown();
                return;
            }

            var settings = _host.Services.GetRequiredService<SettingsService>();
            if (settings.LoadResult.WasCorrupt)
            {
                logger.LogWarning("Configuracoes corrompidas: valem os padroes; copia em {Backup}", settings.LoadResult.BackupPath ?? "(nao foi possivel guardar)");
            }

            settings.SaveIfMissing();
            _host.Services.GetRequiredService<AppWindowPolicy>().Attach(settings, _host.Services.GetRequiredService<LedWatcher>());
            _host.StartAsync().GetAwaiter().GetResult();

            var shell = _host.Services.GetRequiredService<AppShell>();
            _host.Services.GetRequiredService<QuickPanelController>().Attach(shell); // before Start: it raises the hotkey state
            _host.Services.GetRequiredService<MainWindowController>().Attach(shell); // before Start: the first run opens the window
            shell.Start(plan);
            _instance.ListenForActivation(() => Dispatcher.BeginInvoke(shell.RaiseMainWindow));

            // What Windows says about the session (lock, sleep, display, remote) goes to the state that every error line carries, and the marker keeps its heartbeat.
            _sessionTracker = new SessionStateTracker(SessionState.Default, _session, logger);
            _sessionTracker.Start(shell.Messages);
            _session.StartHeartbeat();

            // Both run in the background after the tray is up: the update check waits 5 seconds and never throws (an update is
            // installed when the app is idle, restarting with these same arguments); the old-app warning waits for the dispatcher
            // to be idle.
            _host.Services.GetRequiredService<UpdateService>().Start(e.Args);
            StartUiWatchdog(logger);
            _host.Services.GetRequiredService<AlertaIBT.Core.Backup.AutoBackupService>().Start(); // the automatic copy of the settings, when it is on
            var host = _host;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => WarnAboutOldApp(host, paths)));
        }
        catch (Exception ex)
        {
            // A start that fails must not leave a live process without a tray icon holding the lock.
            logger.LogCritical(ex, "Falha ao iniciar o IBT Controller");
            if (_host is null)
            {
                Serilog.Log.Fatal(ex, "Falha ao iniciar o IBT Controller");
            }

            _session?.End("falha ao iniciar");
            _instance?.Dispose();
            _instance = null;
            Serilog.Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        var closing = _host?.Services.GetService<ILogger<App>>();
        _watchdog?.Dispose(); // first: the dispatcher is going away and would never answer its next ping
        _watchdog = null;
        _episodeTimer?.Stop();
        _sessionTracker?.Dispose();

        // A quit that freezes must not leave a process with no window holding the single-instance lock (2026-11-06 and 11-08: "encerrando" and then nothing, for a day).
        // Closing normally takes well under a second; if it is not done in 15 s the app says where it stood and ends the process by force.
        _shutdownGuard = new ShutdownGuard(TimeSpan.FromSeconds(15), OnShutdownStuck);
        _shutdownGuard.Begin();
        closing?.LogInformation("IBT Controller encerrando ({Reason}, codigo {Code})", _exitReason ?? ExitReasonFromMarkers(), e.ApplicationExitCode);
        try
        {
            if (_host is not null && _restartingForUpdate)
            {
                ShutdownSteps.Run("host (atualizacao)", _host.Dispose); // never started: the updater is about to replace the program
            }
            else if (_host is not null)
            {
                var services = _host.Services;
                ShutdownSteps.Run("atualizacoes", () => services.GetRequiredService<UpdateService>().Dispose());
                ShutdownSteps.Run("shell", () => services.GetRequiredService<AppShell>().Dispose());
                ShutdownSteps.Run("janela principal", () => services.GetRequiredService<MainWindowController>().Dispose()); // saves a setting still inside its 600 ms
                ShutdownSteps.Run("configuracoes", () => services.GetRequiredService<SettingsService>().Dispose()); // saves a change still waiting for its delay
                ShutdownSteps.RunBounded("servicos em segundo plano", () => _host.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(), TimeSpan.FromSeconds(6));
            }
        }
        finally
        {
            if (_host is not null && !_restartingForUpdate)
            {
                ShutdownSteps.RunBounded("liberar servicos", _host.Dispose, TimeSpan.FromSeconds(6));
            }

            closing?.LogInformation("IBT Controller encerrado");
            _session?.End(_exitReason ?? ExitReasonFromMarkers());
            _shutdownGuard?.Complete();
            Serilog.Log.CloseAndFlush();
            _instance?.Dispose();
            base.OnExit(e);
        }
    }

    // The closing took too long (a call to Windows that never came back): say where, record it for the next start, and end the process now.
    private void OnShutdownStuck(string step, TimeSpan took)
    {
        try
        {
            _host?.Services.GetService<ILogger<App>>()?.LogCritical(
                "O encerramento travou ha {Seconds:0} s no passo \"{Step}\": o processo sera encerrado a forca. Ultimas operacoes: {Trail} [{Counters}] [{Session}]",
                took.TotalSeconds,
                step.Length == 0 ? "(nenhum)" : step,
                BreadcrumbTrail.Default.Format(DateTimeOffset.UtcNow),
                ProcessCounters.Describe(),
                SessionState.Default.Describe());
        }
        catch (Exception)
        {
            // The log is not worth stopping the exit for.
        }

        _session?.End("encerramento travado em " + (step.Length == 0 ? "(nenhum passo)" : step));
        Serilog.Log.CloseAndFlush();
        var killer = new Thread(() =>
        {
            Thread.Sleep(3000); // Environment.Exit waits for the process exit handlers: if one of them is stuck too, the process is killed
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        })
        { IsBackground = true, Name = "encerramento forcado" };
        killer.Start();
        Environment.Exit(0);
    }

    // Says, in the log and once, how the run before this one ended: a Windows shutdown that killed it before it could write its end, a power cut, a crash, a PC that
    // slept and never came back, a freeze on quitting... The Windows event log is read in the background (read-only, no admin rights, given up after 8 s).
    private static void ExplainPreviousRun(PreviousRun previous, ILogger logger)
    {
        var worth = previous.Existed
            && (!previous.EndedCleanly || previous.State == "session-ending" || previous.CrashType is not null
                || previous.EndReason?.StartsWith("encerramento travado", StringComparison.Ordinal) == true);
        if (!worth)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var from = (previous.StartedAt ?? now.AddDays(-1)) - TimeSpan.FromMinutes(1);
                var (events, available) = WindowsEventLog.Read(from, now, TimeSpan.FromSeconds(8));
                var report = RunEndDiagnosis.Describe(previous, events, available);
                if (report.Kind == RunEndKind.Clean)
                {
                    return;
                }

                if (report.IsWarning)
                {
                    logger.LogWarning("{Report}", report.Message);
                }
                else
                {
                    logger.LogInformation("{Report}", report.Message);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogWarning(ex, "Nao foi possivel analisar como a execucao anterior terminou");
            }
        });
    }

    /// <inheritdoc />
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows is shutting down or logging off: this is a normal end, and the log should say who asked.
        _exitReason = "Windows encerrando a sessao (" + e.ReasonSessionEnding + ")";
        // Written at once: Windows may kill the process before OnExit runs (three normal shutdowns were reported as crashes because of that).
        _session?.SessionEnding(_exitReason);
        _host?.Services.GetService<ILogger<App>>()?.LogInformation("{Reason}", _exitReason);
        base.OnSessionEnding(e);
    }

    // The update installer writes its restart marker just before it asks the app to leave.
    private string ExitReasonFromMarkers() =>
        _restartingForUpdate || File.Exists(AppPaths.Default.UpdateRestartFile) ? "reiniciando para instalar a atualizacao" : "saida normal";

    private static string RunningVersion()
    {
        var text = typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
            ?? typeof(App).Assembly.GetName().Version?.ToString(3)
            ?? "?";
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? text[..plus] : text;
    }

    // A background timer asks the dispatcher to answer; a stall of more than 2 s is logged with what the app did last (see UiStallWatchdog).
    private void StartUiWatchdog(ILogger logger)
    {
        _watchdog = new UiStallWatchdog(
            action =>
            {
                if (Dispatcher.HasShutdownStarted)
                {
                    throw new InvalidOperationException("The dispatcher is closing.");
                }

                // Input priority: what the person feels. Normal would answer even when typing and clicking are stuck behind rendering.
                Dispatcher.BeginInvoke(DispatcherPriority.Input, action);
            },
            logger,
            context: ProcessCounters.Describe);
        _watchdog.Start();
    }

    // The one-time warning about the old Electron app. Not in development builds: the developer's PC keeps the old app on purpose.
    private static void WarnAboutOldApp(IHost host, AppPaths paths)
    {
#if !DEBUG
        var policy = host.Services.GetRequiredService<AppWindowPolicy>();
        var notice = new LegacyAppNotice(
            host.Services.GetRequiredService<LegacyAppDetector>(),
            System.IO.Path.Combine(paths.DataFolder, "legacy-notice-shown.txt"),
            prompt => Views.NoticeWindow.Ask(prompt.Title, prompt.Message, prompt.OfferRemoval, policy),
            host.Services.GetRequiredService<ILogger<App>>());
        notice.Run();
#endif
    }

    private void InstallErrorLogging(ILogger logger)
    {
        // WPF's own "not enough quota" (error 1816) while it handles a window position change is not our bug and never fatal: one warning per episode, one line when it ends
        // (see UiErrorHandler). Every unhandled dispatcher exception is handled: the app never dies from one.
        _uiErrors = new UiErrorHandler(logger, _failures, SessionState.Default, ProcessCounters.Describe);
        _episodeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _episodeTimer.Tick += (_, _) => _uiErrors.EndQuietEpisodes(() => _host?.Services.GetService<AppShell>()?.ReapplyWindows("falha do sistema de janelas"));
        _episodeTimer.Start();

        DispatcherUnhandledException += (_, args) => args.Handled = _uiErrors.Handle(args.Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Breadcrumbs.Add("erro em tarefa em segundo plano: " + args.Exception.GetBaseException().GetType().Name);
            logger.LogError(args.Exception, "Erro em tarefa em segundo plano. Ultimas operacoes: {Trail}", BreadcrumbTrail.Default.Format(DateTimeOffset.UtcNow));
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                logger.LogCritical(
                    args.ExceptionObject as Exception,
                    "Erro fatal (o app vai fechar: {Terminating}) [{Counters}]. Ultimas operacoes: {Trail}",
                    args.IsTerminating,
                    ProcessCounters.Describe(),
                    BreadcrumbTrail.Default.Format(DateTimeOffset.UtcNow));
            }
            catch (Exception)
            {
                // Nothing more can be done while crashing.
            }

            // The marker says "crash" (the TYPE of the exception only): the next start tells a crash from a power cut or a Task Manager kill.
            try
            {
                _session?.Crash((args.ExceptionObject as Exception)?.GetType().Name ?? "(desconhecida)");
            }
            catch (Exception)
            {
                // Nothing more can be done while crashing.
            }

            // Give the mouse back before the process dies (Windows also releases it when the process ends).
            try
            {
                _host?.Services.GetService<MouseLockController>()?.ReleaseNow();
            }
            catch (Exception)
            {
                // Nothing more can be done while crashing.
            }

            Serilog.Log.CloseAndFlush(); // synchronous: the last line is on disk before the process dies
        };
    }
}

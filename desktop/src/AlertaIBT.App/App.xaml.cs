using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AlertaIBT.App.Hosting;
using AlertaIBT.App.Platform;
using AlertaIBT.App.Tray;
using AlertaIBT.App.Views;
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

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplicationAccentColorManager.Apply(Color.FromRgb(0x1D, 0xA3, 0x5F), ApplicationTheme.Dark, false, false);
        var arguments = StartupArguments.Parse(e.Args);

        var paths = AppPaths.Default;

        // Just restarted by an update: the old process is leaving (the updater waits for it), so wait for the lock a few seconds
        // instead of taking it for "another copy is running" and exiting. A normal start waits not at all.
        _instance = SingleInstance.TryAcquire(UpdateRestartMarker.ConsumeWait(paths.UpdateRestartFile, DateTimeOffset.UtcNow));
        if (_instance is null)
        {
            SingleInstance.SignalExisting(); // the running copy shows its window
            Shutdown();
            return;
        }

        ILogger logger = NullLogger<App>.Instance;
        try
        {
            _host = AppHost.Build(paths, arguments);
            logger = _host.Services.GetRequiredService<ILogger<App>>();
            InstallErrorLogging(logger);

            // An update downloaded earlier is installed now, at the start (nothing is serving yet). The single-instance
            // lock is already ours, so a second launch of a running app never gets here.
            if (_host.Services.GetRequiredService<UpdateService>().TryApplyPendingAtStartup(e.Args))
            {
                _restartingForUpdate = true;
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
            _host.StartAsync().GetAwaiter().GetResult();

            var shell = _host.Services.GetRequiredService<AppShell>();
            _host.Services.GetRequiredService<QuickPanelController>().Attach(shell); // before Start: it raises the hotkey state
            _host.Services.GetRequiredService<MainWindowController>().Attach(shell); // before Start: a launch without --tray opens the window
            shell.Start();
            _instance.ListenForActivation(() => Dispatcher.BeginInvoke(shell.RaiseMainWindow));

            // Both run in the background after the tray is up: the update check waits 5 seconds and never throws (an update is
            // installed when the app is idle, restarting with these same arguments); the old-app warning waits for the dispatcher
            // to be idle.
            _host.Services.GetRequiredService<UpdateService>().Start(e.Args);
            var host = _host;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => WarnAboutOldApp(host, paths)));
        }
        catch (Exception ex)
        {
            // A start that fails must not leave a live process without a tray icon holding the lock.
            logger.LogCritical(ex, "Falha ao iniciar o Alerta-IBT");
            if (_host is null)
            {
                Serilog.Log.Fatal(ex, "Falha ao iniciar o Alerta-IBT");
            }

            _instance?.Dispose();
            _instance = null;
            Serilog.Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null && _restartingForUpdate)
            {
                _host.Dispose(); // never started: the updater is about to replace the program
            }
            else if (_host is not null)
            {
                try
                {
                    _host.Services.GetRequiredService<UpdateService>().Dispose();
                    _host.Services.GetRequiredService<AppShell>().Dispose();
                    _host.Services.GetRequiredService<MainWindowController>().Dispose(); // saves a setting still inside its 600 ms
                    _host.Services.GetRequiredService<SettingsService>().Dispose(); // saves a change still waiting for its delay
                    _host.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _host.Services.GetRequiredService<ILogger<App>>().LogError(ex, "Erro ao encerrar o Alerta-IBT");
                }
                finally
                {
                    _host.Dispose();
                }
            }
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
            _instance?.Dispose();
            base.OnExit(e);
        }
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
        DispatcherUnhandledException += (_, args) =>
        {
            logger.LogError(args.Exception, "Erro na interface");
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            logger.LogError(args.Exception, "Erro em tarefa em segundo plano");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            logger.LogCritical(args.ExceptionObject as Exception, "Erro fatal");
            Serilog.Log.CloseAndFlush();
        };
    }
}

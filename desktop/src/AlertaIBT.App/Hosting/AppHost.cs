using System.IO;
using System.Net.Http;
using System.Windows;
using AlertaIBT.App.Overlay;
using AlertaIBT.App.Platform;
using AlertaIBT.App.Services;
using AlertaIBT.App.Tray;
using AlertaIBT.App.ViewModels;
using AlertaIBT.App.Views;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Telegram;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace AlertaIBT.App.Hosting;

/// <summary>Builds the dependency-injection host: logging to <c>%AppData%\AlertaIBT\logs</c> (7 days) and the app's services.</summary>
public static class AppHost
{
    /// <summary>Creates the host (not started). Also configures the global Serilog logger.</summary>
    /// <param name="paths">Where the app keeps its files.</param>
    /// <param name="arguments">The command line.</param>
    /// <param name="apiPort">The port of the local control API; 8787 in the app, a free port in tests.</param>
    public static IHost Build(AppPaths paths, StartupArguments arguments, int apiPort = HttpControlServer.DefaultPort)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(arguments);

        Directory.CreateDirectory(paths.LogsFolder);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(paths.LogsFolder, "alertaibt-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
            DisableDefaults = true,
        });
        builder.Services.AddSerilog();
        builder.Services.Configure<ConsoleLifetimeOptions>(options => options.SuppressStatusMessages = true);
        ConfigureServices(builder.Services, paths, arguments, apiPort);
        return builder.Build();
    }

    /// <summary>Registers the services. Public so a test can build the same graph.</summary>
    public static void ConfigureServices(IServiceCollection services, AppPaths paths, StartupArguments arguments, int apiPort = HttpControlServer.DefaultPort)
    {
        services.AddSingleton(paths);
        services.AddSingleton(arguments);
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<SettingsService>>();
            return new SettingsService(
                new SettingsStore(paths.SettingsFile),
                onError: ex => logger.LogError(ex, "Problema com o arquivo de configurações"));
        });
        services.AddSingleton<IRunKeyStore, WindowsRunKeyStore>();
        services.AddSingleton<IUpdateClient>(_ => new VelopackUpdateClient());
        services.AddSingleton(provider => new UpdateService(
            provider.GetRequiredService<IUpdateClient>(),
            provider.GetRequiredService<ILogger<UpdateService>>(),
            Path.Combine(paths.DataFolder, "update-attempt.txt")));
        services.AddSingleton(provider => new LegacyAppDetector(
            provider.GetRequiredService<IRunKeyStore>(),
            LegacyAppDetector.IsOldProcessRunning));
        services.AddSingleton(provider => new StartupRegistration(
            provider.GetRequiredService<IRunKeyStore>(),
            Environment.ProcessPath,
#if DEBUG
            enabled: false)); // a development build must not register its own build folder as the autostart program
#else
            enabled: true));
#endif
        services.AddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<SettingsService>();
            return new LedTracker(MonitorService.Enumerate, () => settings.Current.System.LastLedDevicePath, arguments.TestLed);
        });
        services.AddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<SettingsService>();
            var logger = provider.GetRequiredService<ILogger<LedWatcher>>();
            return new LedWatcher(
                provider.GetRequiredService<LedTracker>(),
                path => settings.Update(s => string.Equals(s.System.LastLedDevicePath, path, StringComparison.OrdinalIgnoreCase)
                    ? s
                    : s with { System = s.System with { LastLedDevicePath = path } }),
                ex => logger.LogError(ex, "Erro ao procurar o LED"));
        });
        services.AddSingleton<ILedSource>(provider => provider.GetRequiredService<LedWatcher>());

        // Every window of the app but the bar: out of screen capture, and never on the LED monitor.
        services.AddSingleton(provider => new AppWindowPolicy(
            () => provider.GetRequiredService<LedWatcher>().Led,
            provider.GetRequiredService<ILogger<AppWindowPolicy>>()));
        services.AddSingleton<IFrameSource, CompositionFrameSource>();
        services.AddSingleton<IUiDispatcher>(_ => new WpfUiDispatcher(Application.Current.Dispatcher));
        services.AddSingleton(provider => new OverlayController(
            provider.GetRequiredService<SettingsService>(),
            provider.GetRequiredService<ILedSource>(),
            () => new OverlayWindow(),
            provider.GetRequiredService<IFrameSource>(),
            provider.GetRequiredService<IUiDispatcher>(),
            provider.GetRequiredService<ILogger<OverlayController>>()));
        ConfigureAlertServices(services, paths, apiPort);
        services.AddSingleton<TrayService>();
        services.AddSingleton<AppShell>();
        ConfigureQuickPanel(services);
        ConfigureMainWindow(services, paths);
    }

    // The quick panel: its logic, and the controller that opens and closes its window.
    private static void ConfigureQuickPanel(IServiceCollection services)
    {
        services.AddSingleton<IAlertGateway>(provider => new AlertGateway(
            provider.GetRequiredService<AlertService>(),
            provider.GetRequiredService<AlertRuntime>()));
        services.AddSingleton(provider =>
        {
            var overlay = provider.GetRequiredService<OverlayController>();
            return new QuickPanelViewModel(
                provider.GetRequiredService<IAlertGateway>(),
                provider.GetRequiredService<StatusHub>(),
                () => overlay.PassProgress,
                PostToUi,
                logger: provider.GetRequiredService<ILogger<QuickPanelViewModel>>());
        });
        services.AddSingleton(provider =>
        {
            var viewModel = provider.GetRequiredService<QuickPanelViewModel>();
            return new QuickPanelController(
                viewModel,
                () => new QuickPanel(viewModel, provider.GetRequiredService<AppWindowPolicy>()),
                provider.GetRequiredService<ILogger<QuickPanelController>>());
        });
    }

    // The main window: one autosave for the two settings tabs, the tabs' view-models, and the controller that opens the window.
    private static void ConfigureMainWindow(IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(_ => new LogFiles(paths.LogsFolder));
        services.AddSingleton(provider => new Autosaver(
            provider.GetRequiredService<SettingsService>(),
            PostToUi,
            logger: provider.GetRequiredService<ILogger<Autosaver>>()));
        services.AddSingleton(provider => new HomeViewModel(
            provider.GetRequiredService<IAlertGateway>(),

            // The window has its own message field, apart from the one of the quick panel: two drafts, the same behavior.
            new QuickPanelViewModel(
                provider.GetRequiredService<IAlertGateway>(),
                provider.GetRequiredService<StatusHub>(),
                () => provider.GetRequiredService<OverlayController>().PassProgress,
                PostToUi,
                logger: provider.GetRequiredService<ILogger<QuickPanelViewModel>>()),
            PostToUi,
            logger: provider.GetRequiredService<ILogger<HomeViewModel>>()));
        services.AddSingleton(provider => new CorrectionViewModel(
            provider.GetRequiredService<SettingsService>(),
            provider.GetRequiredService<ISecretProtector>(),
            provider.GetRequiredService<StatusHub>(),
            provider.GetRequiredService<Autosaver>(),
            PostToUi));
        services.AddSingleton(provider => new DiagnosticsViewModel(
            provider.GetRequiredService<LogFiles>(),
            text => Clipboard.SetText(text),
            PickLogExportPath,
            provider.GetRequiredService<ILogger<DiagnosticsViewModel>>()));
        services.AddSingleton(provider => new SettingsViewModel(
            provider.GetRequiredService<SettingsService>(),
            provider.GetRequiredService<ISecretProtector>(),
            provider.GetRequiredService<StatusHub>(),
            provider.GetRequiredService<Autosaver>(),
            provider.GetRequiredService<DiagnosticsViewModel>(),
            provider.GetRequiredService<OverlayController>().PreviewAsync,
            PostToUi,
            provider.GetRequiredService<ILogger<SettingsViewModel>>()));
        services.AddSingleton(provider => new MainViewModel(
            provider.GetRequiredService<StatusHub>(),
            provider.GetRequiredService<HomeViewModel>(),
            provider.GetRequiredService<CorrectionViewModel>(),
            provider.GetRequiredService<SettingsViewModel>(),
            provider.GetRequiredService<Autosaver>(),
            PostToUi));
        services.AddSingleton(provider =>
        {
            var viewModel = provider.GetRequiredService<MainViewModel>();
            return new MainWindowController(
                viewModel,
                () => new MainWindow(viewModel, provider.GetRequiredService<AppWindowPolicy>()),
                provider.GetRequiredService<ILogger<MainWindowController>>());
        });
    }

    // "Exportar" in Diagnostico: asks where to save the logs; null when the user cancels.
    private static string? PickLogExportPath()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar logs",
            FileName = $"alertaibt-logs-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            DefaultExt = ".txt",
            Filter = "Texto (*.txt)|*.txt",
        };


        // Over the main window, which is never on the LED. (The system file dialog cannot be hidden from capture before it shows.)
        var owner = Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault(window => window.IsVisible);
        return (owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true ? dialog.FileName : null;
    }

    // Changes announced by the status hub or the queue come from any thread; the panel's properties belong to the UI thread.
    private static void PostToUi(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            dispatcher.BeginInvoke(action);
        }
        else
        {
            action();
        }
    }

    // The alert pipeline: queue on disk, correction, Telegram, the service that plays the queue, and the local API.
    private static void ConfigureAlertServices(IServiceCollection services, AppPaths paths, int apiPort)
    {
        services.AddSingleton<StatusHub>();
        services.AddSingleton(provider => new QueueStore(
            paths.QueueFile,
            onError: ex => provider.GetRequiredService<ILogger<QueueStore>>().LogError(ex, "Problema com o arquivo da fila")));
        services.AddSingleton(provider => AlertQueue.FromSnapshot(provider.GetRequiredService<QueueStore>().Load(), TimeProvider.System));

        // The Telegram client keeps its own HttpClient (long polls last 25 s); Gemini gets one long-lived client with warm connections.
        services.AddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<SettingsService>();
            var protector = provider.GetRequiredService<ISecretProtector>();
            return new TelegramClient(new HttpClient(), () => settings.Current.GetTelegramToken(protector));
        });
        services.AddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<SettingsService>();
            return new TelegramPoller(
                provider.GetRequiredService<TelegramClient>(),
                () => settings.Current.Telegram.ChatId,
                offset => settings.Update(s => s.Telegram.Offset == offset ? s : s with { Telegram = s.Telegram with { Offset = offset } }),
                settings.Current.Telegram.Offset);
        });
        services.AddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<SettingsService>();
            return new TelegramGroupNotifier(
                provider.GetRequiredService<TelegramClient>(),
                () => settings.Current.Telegram.ChatId,
                provider.GetRequiredService<ILogger<TelegramGroupNotifier>>());
        });
        services.AddSingleton<IGroupNotifier>(provider => provider.GetRequiredService<TelegramGroupNotifier>());

        services.AddSingleton(provider =>
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Dictionaries");
            var hub = provider.GetRequiredService<StatusHub>();
            return new LazyCorrector(
                () => OfflineCorrector.Load(directory),
                provider.GetRequiredService<ILogger<LazyCorrector>>(),
                directory,
                ex => hub.ReportError("Dicionario offline", ex));
        });
        services.AddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<SettingsService>();
            var protector = provider.GetRequiredService<ISecretProtector>();
            return new GeminiCorrector(
                GeminiHttp.CreateClient(),
                () => settings.Current.GetGeminiKey(protector),
                () => settings.Current.Correction.GeminiModel,
                provider.GetRequiredService<ILogger<GeminiCorrector>>());
        });
        services.AddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<SettingsService>();
            var rules = new FixedRulesProvider(() => settings.Current.Correction.FixedRules);
            return new CorrectionPipeline(
                provider.GetRequiredService<GeminiCorrector>(),
                provider.GetRequiredService<LazyCorrector>(),
                rules.Current,
                CorrectionPipeline.DefaultGeminiTimeout,
                logger: provider.GetRequiredService<ILogger<CorrectionPipeline>>(),
                model: () => settings.Current.Correction.GeminiModel);
        });

        services.AddSingleton<OverlayDisplay>();
        services.AddSingleton<IAlertDisplay>(provider => provider.GetRequiredService<OverlayDisplay>());
        services.AddSingleton(provider => new AlertService(
            provider.GetRequiredService<AlertQueue>(),
            provider.GetRequiredService<CorrectionPipeline>(),
            provider.GetRequiredService<IAlertDisplay>(),
            provider.GetRequiredService<IGroupNotifier>(),
            () => provider.GetRequiredService<SettingsService>().Current,
            provider.GetRequiredService<StatusHub>(),
            provider.GetRequiredService<ILogger<AlertService>>(),
            filter: new ProfanityFilter(paths.BlockedWordsFile, provider.GetRequiredService<ILogger<ProfanityFilter>>())));
        services.AddSingleton<IOverlayControl>(provider => provider.GetRequiredService<AlertService>());
        services.AddSingleton(provider => new TelegramEngine(
            provider.GetRequiredService<TelegramPoller>(),
            provider.GetRequiredService<AlertService>(),
            provider.GetRequiredService<IGroupNotifier>(),
            provider.GetRequiredService<StatusHub>(),
            provider.GetRequiredService<ILogger<TelegramEngine>>()));
        services.AddSingleton(provider => new HttpControlServer(
            provider.GetRequiredService<IOverlayControl>(),
            provider.GetRequiredService<StatusHub>(),
            provider.GetRequiredService<ILogger<HttpControlServer>>(),
            apiPort));
        services.AddSingleton<AlertRuntime>();
    }
}

using System.Net;
using System.Net.Sockets;
using AlertaIBT.App.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AlertaIBT.App.Tests.Hosting;

public sealed class StartupPlanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-plan-" + Guid.NewGuid().ToString("N"));

    public StartupPlanTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Serilog.Log.CloseAndFlush();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A log file still held by the logger: the OS temp cleaner takes it.
        }
    }

    private static StartupArguments Args(bool tray) => new(Tray: tray, TestLed: false);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_app_always_starts_hidden_whatever_the_switch_when_it_is_not_the_first_run(bool tray)
    {
        var plan = StartupPlan.Decide(Args(tray), firstRun: false, alreadyRunning: false);

        Assert.False(plan.ShowWindow);
    }

    [Fact]
    public void A_manual_launch_says_once_in_the_tray_that_it_started_and_the_startup_entry_stays_quiet()
    {
        Assert.True(StartupPlan.Decide(Args(tray: false), firstRun: false, alreadyRunning: false).ShowTrayBalloon);
        Assert.False(StartupPlan.Decide(Args(tray: true), firstRun: false, alreadyRunning: false).ShowTrayBalloon);
    }

    [Fact]
    public void A_restart_after_an_update_is_silent_even_without_the_switch()
    {
        var plan = StartupPlan.Decide(Args(tray: false), firstRun: false, alreadyRunning: false, restartedByUpdate: true);

        Assert.False(plan.ShowWindow);
        Assert.False(plan.ShowTrayBalloon);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_first_run_after_an_installation_shows_the_window_once_and_no_balloon(bool tray)
    {
        var plan = StartupPlan.Decide(Args(tray), firstRun: true, alreadyRunning: false);

        Assert.True(plan.ShowWindow);
        Assert.False(plan.ShowTrayBalloon);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void A_second_launch_while_the_app_runs_still_brings_the_window_up(bool tray, bool firstRun)
    {
        var plan = StartupPlan.Decide(Args(tray), firstRun, alreadyRunning: true);

        Assert.True(plan.ShowWindow);
        Assert.False(plan.ShowTrayBalloon);
    }

    [Fact]
    public void The_first_run_is_a_data_folder_without_settings_json()
    {
        var paths = new AppPaths(_dir);
        Assert.True(StartupPlan.IsFirstRun(paths));

        File.WriteAllText(paths.SettingsFile, "{}");

        Assert.False(StartupPlan.IsFirstRun(paths));
    }

    [Fact]
    public void The_balloon_text_is_Alerta_IBT_esta_na_bandeja()
    {
        Assert.Equal("Alerta-IBT est\U000000E1 na bandeja", StartupPlan.BalloonMessage);
    }

    [Fact]
    public void The_tray_switch_is_still_accepted()
    {
        Assert.True(StartupArguments.Parse(["--tray"]).Tray);
        Assert.False(StartupArguments.Parse([]).Tray);
    }

    // The shell with the real graph, not started: ApplyStartupPlan is what Start ends with.
    private AppShell Shell(Microsoft.Extensions.Hosting.IHost host) => host.Services.GetRequiredService<AppShell>();

    private Microsoft.Extensions.Hosting.IHost Host()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return AppHost.Build(new AppPaths(_dir), new StartupArguments(Tray: false, TestLed: false), port);
    }

    [Fact]
    public void A_hidden_plan_raises_no_window_request_and_a_manual_launch_gets_exactly_one_balloon()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host();
            var shell = Shell(host);
            var windows = 0;
            shell.MainWindowRequested += () => windows++;
            var balloons = new List<(string Title, string Message)>();

            shell.ApplyStartupPlan(StartupPlan.Decide(Args(tray: false), firstRun: false, alreadyRunning: false), (t, m) => balloons.Add((t, m)));

            Assert.Equal(0, windows); // no Show/Activate path: Holyrics and OBS keep the focus
            Assert.Equal([("Alerta-IBT", "Alerta-IBT est\U000000E1 na bandeja")], balloons);
        });
    }

    [Fact]
    public void The_startup_entry_launch_shows_nothing_at_all()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host();
            var shell = Shell(host);
            var windows = 0;
            shell.MainWindowRequested += () => windows++;
            var balloons = 0;

            shell.ApplyStartupPlan(StartupPlan.Decide(Args(tray: true), firstRun: false, alreadyRunning: false), (_, _) => balloons++);

            Assert.Equal(0, windows);
            Assert.Equal(0, balloons);
        });
    }

    [Fact]
    public void The_first_run_raises_the_window_request_once()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host();
            var shell = Shell(host);
            var windows = 0;
            shell.MainWindowRequested += () => windows++;
            var balloons = 0;

            shell.ApplyStartupPlan(StartupPlan.Decide(Args(tray: false), firstRun: true, alreadyRunning: false), (_, _) => balloons++);

            Assert.Equal(1, windows);
            Assert.Equal(0, balloons);
        });
    }
}

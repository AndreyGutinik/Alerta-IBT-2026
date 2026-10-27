using AlertaIBT.App.Hosting;
using AlertaIBT.App.Platform;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Player;
using Microsoft.Extensions.DependencyInjection;

namespace AlertaIBT.App.Tests.Hosting;

// The real graph of the app: the player's parts are wired, and building the graph plays nothing and opens no device.
public sealed class PlayerGraphTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-playergraph-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Serilog.Log.CloseAndFlush();
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // A log file still held by the logger: the OS temp cleaner takes it.
        }
    }

    [Fact]
    public void The_player_parts_resolve_share_one_engine_and_start_stopped()
    {
        WpfTestHost.Run(() =>
        {
            using var host = AppHost.Build(new AppPaths(_dir), new StartupArguments(Tray: true, TestLed: true));
            var services = host.Services;

            var player = services.GetRequiredService<MusicPlayer>();
            var viewModel = services.GetRequiredService<PlayerViewModel>();
            var quick = services.GetRequiredService<QuickPanelViewModel>();

            Assert.Same(player, services.GetRequiredService<AppShell>().Player);
            Assert.Same(viewModel, quick.Player);
            Assert.NotNull(services.GetRequiredService<AlarmScheduler>());
            Assert.NotNull(services.GetRequiredService<MediaKeyRouter>());
            Assert.IsType<NAudioPlayerBackend>(services.GetRequiredService<IPlayerBackend>());
            Assert.IsType<WindowsAudioSystem>(services.GetRequiredService<IAudioSystem>());
            Assert.Equal(PlayerState.Stopped, player.GetStatus().State);
            Assert.Empty(services.GetRequiredService<PlaylistLibrary>().Tracks("Igreja")); // nothing is scanned before Start
            Assert.True(quick.ShowPlayer);
        });
    }
}

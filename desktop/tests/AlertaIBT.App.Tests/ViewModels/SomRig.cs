using AlertaIBT.App.Services;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Loudness;
using AlertaIBT.Core.Player;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.ViewModels;

/// <summary>
/// The Som tab on the real engines with fakes at the edges: settings in a temp folder, a fake Windows audio, a fake player backend and a fake
/// per-app output. No device is opened, no file is decoded, no sound, no real volume or output is touched.
/// </summary>
internal sealed class SomRig : IDisposable
{
    private readonly PlaylistLibrary _library;

    public SomRig(AppSettings? initial = null, ICoverArtLoader? covers = null, ITrackMetadataReader? metadata = null, bool withLoudness = false)
    {
        Settings = new SettingsRig(initial);
        _library = new PlaylistLibrary(new FakeFiles(), _ => null, Settings.Clock, action => action());
        if (withLoudness)
        {
            Loudness = new LoudnessService(Settings.Settings, _library, Backend, Path.Combine(Path.GetTempPath(), "ibt-somrig-" + Guid.NewGuid().ToString("N"), "loudness-cache.json"), new FlacLoudnessTagWriter(), Settings.Clock);
        }

        Player = new MusicPlayer(Settings.Settings, _library, Backend, Audio, metadata ?? new FakeMetadata(), Settings.Clock, null, action => action(), new Random(1), loudness: Loudness?.Scanner);
        Alarms = new AlarmScheduler(Settings.Settings, Player, Settings.Clock);
        Player.Start();
        PlayerView = new PlayerViewModel(Settings.Settings, Player, Alarms, Audio, action => action(), action => action(), Settings.Clock, covers: covers, loudness: Loudness);
        Controller = new AudioController(Audio, Settings.Settings, Settings.Clock, null, AppOutput, action => action());
        Som = new SomViewModel(
            Settings.Settings,
            Settings.Saver,
            Controller,
            PlayerView,
            () => Folders.Count > 0 ? Folders.Dequeue() : null,
            confirm: (title, message) =>
            {
                Confirmations.Add((title, message));
                return ConfirmAnswer;
            },
            askName: (title, current, check) =>
            {
                NameRequests.Add((title, current));
                var answer = Names.Count > 0 ? Names.Dequeue() : null;
                // like the real box: a name with a problem never gets through
                return answer is not null && check(answer) is null ? answer.Trim() : null;
            });
    }

    public SettingsRig Settings { get; }

    /// <summary>The loudness service (only when the rig was made with it); never started, so no thread runs.</summary>
    public LoudnessService? Loudness { get; }

    public FakeAudio Audio { get; } = new();

    public FakePlayerBackend Backend { get; } = new();

    public FakeAppOutputPolicy AppOutput { get; } = new();

    public MusicPlayer Player { get; }

    public AlarmScheduler Alarms { get; }

    public PlayerViewModel PlayerView { get; }

    public AudioController Controller { get; }

    public SomViewModel Som { get; }

    /// <summary>The questions the Sim / Nao window was asked (title, message), in order.</summary>
    public List<(string Title, string Message)> Confirmations { get; } = [];

    /// <summary>What the Sim / Nao window answers (true is Sim).</summary>
    public bool ConfirmAnswer { get; set; } = true;

    /// <summary>The name box was opened for (title, current name), in order.</summary>
    public List<(string Title, string Current)> NameRequests { get; } = [];

    /// <summary>What the name box answers, in order (empty means the user cancelled).</summary>
    public Queue<string?> Names { get; } = new();

    /// <summary>What the folder picker answers, in order (empty means the user cancelled).</summary>
    public Queue<string?> Folders { get; } = new();

    public AppSettings Current => Settings.Settings.Current;

    /// <summary>Starts the audio rules and lets their timers run: the first look, and the first setting of the volumes.</summary>
    public void BootAudio()
    {
        Controller.Start();
        Settings.Clock.Advance(AudioController.StartDelay + AudioController.Debounce);
    }

    /// <summary>The autosave rests for its 600 ms: what was typed is saved.</summary>
    public void Rest() => Settings.Rest();

    public void Dispose()
    {
        Som.Dispose();
        Controller.Dispose();
        PlayerView.Dispose();
        Alarms.Dispose();
        Player.Dispose();
        Loudness?.Dispose();
        _library.Dispose();
        Settings.Dispose();
    }
}

/// <summary>The per-app output of Windows for the tests: plain data, never touches a real program.</summary>
internal sealed class FakeAppOutputPolicy : IAppOutputPolicy
{
    public Dictionary<int, string> Endpoints { get; } = [];

    public string? GetPersistedEndpoint(int processId) => Endpoints.TryGetValue(processId, out var id) ? id : string.Empty;

    public bool SetPersistedEndpoint(int processId, string? endpointId)
    {
        if (endpointId is null)
        {
            Endpoints.Remove(processId);
        }
        else
        {
            Endpoints[processId] = endpointId;
        }

        return true;
    }
}

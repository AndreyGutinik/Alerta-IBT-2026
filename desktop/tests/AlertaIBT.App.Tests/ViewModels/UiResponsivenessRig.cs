using System.Diagnostics;
using System.Windows.Threading;
using AlertaIBT.App.Platform;
using AlertaIBT.App.ViewModels;
using AlertaIBT.App.Tests.Player;
using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Loudness;
using AlertaIBT.Core.Player;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.ViewModels;

/// <summary>
/// The player side of the app on real parts (real settings file in a temp folder, real folder watcher, real decoder, the real loudness scanner and
/// tag writer, the real view-model) with only the sound card replaced by a thread that pulls the mixer at the pace of a sound card. It runs on its
/// own UI thread (a dispatcher made for it) so that the measurement is not polluted by other tests, and it measures that thread: how long any
/// call blocks it and the longest wait of a heartbeat. Nothing outside the temp folders is written.
/// </summary>
internal sealed class UiResponsivenessRig : IDisposable
{
    private readonly Thread _uiThread;
    private readonly string _root;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _heartbeat;
    private Thread? _soundCard;
    private long _maxGapTicks;
    private long _gapOver100;

    public UiResponsivenessRig(string musicFolder, IAudioSystem? audio = null, bool realFolderReadOnly = false, ScannerOptions? scanner = null)
    {
        MusicFolder = musicFolder;
        _root = Path.Combine(Path.GetTempPath(), "ibt-ui-rig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var ready = new ManualResetEventSlim();
        Exception? failure = null;
        _uiThread = new Thread(() =>
        {
            try
            {
                Dispatcher = Dispatcher.CurrentDispatcher;
                Build(audio, scanner);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "rig UI thread",
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        ready.Wait();
        if (failure is not null)
        {
            throw new InvalidOperationException("The rig could not start", failure);
        }
    }

    public string MusicFolder { get; }

    public Dispatcher Dispatcher { get; private set; } = null!;

    public SettingsService Settings { get; private set; } = null!;

    public PlaylistLibrary Library { get; private set; } = null!;

    public LoudnessService Loudness { get; private set; } = null!;

    public MusicPlayer Player { get; private set; } = null!;

    public AlarmScheduler Alarms { get; private set; } = null!;

    public PlayerViewModel View { get; private set; } = null!;

    public CaptureOutput? Output { get; private set; }

    public string CachePath => Path.Combine(_root, "loudness-cache.json");

    public TimeSpan MaxHeartbeatGap => TimeSpan.FromTicks(Interlocked.Read(ref _maxGapTicks));

    public long HeartbeatsOver100Ms => Interlocked.Read(ref _gapOver100);

    private void Build(IAudioSystem? audio, ScannerOptions? scanner)
    {
        Settings = new SettingsService(new SettingsStore(Path.Combine(_root, "settings.json")));
        Library = new PlaylistLibrary(new PhysicalMediaFileSystem(), PhysicalMediaFileSystem.Watch);
        var backend = new PumpBackend(this);
        Loudness = new LoudnessService(Settings, Library, backend, CachePath, new FlacLoudnessTagWriter(), options: scanner);
        Settings.Update(s => s with
        {
            Player = s.Player with
            {
                Enabled = true,
                Playlists = [new PlaylistEntry("Igreja", MusicFolder)],
                CurrentPlaylist = "Igreja",
                Shuffle = false,
                Repeat = "all",
                Volume = 100,
                StartMuted = false,
                FadeInSeconds = 0,
                CrossfadeSeconds = 2,
                NormalizeEnabled = true,
                WriteLoudnessTags = false,
            },
        });
        var system = audio ?? new FakeAudio();
        Player = new MusicPlayer(Settings, Library, backend, system, new FileTrackMetadataReader(), loudness: Loudness.Scanner);
        Loudness.IsInUse = Player.IsInUse;
        Alarms = new AlarmScheduler(Settings, Player);
        Player.Start();
        View = new PlayerViewModel(
            Settings,
            Player,
            Alarms,
            system,
            action => Dispatcher.BeginInvoke(action),
            action => _ = Task.Run(action),
            loudness: Loudness);
    }

    /// <summary>Starts the scanner and waits for the playlist to be read.</summary>
    public void StartScanner()
    {
        Dispatcher.Invoke(Loudness.Start);
        var deadline = Stopwatch.StartNew();
        while (Library.Tracks("Igreja").Count == 0 && deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            Thread.Sleep(20);
        }
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread and returns how long it kept that thread.</summary>
    public TimeSpan OnUi(Action action)
    {
        var elapsed = TimeSpan.Zero;
        Dispatcher.Invoke(() =>
        {
            var watch = Stopwatch.StartNew();
            action();
            elapsed = watch.Elapsed;
        });
        return elapsed;
    }

    /// <summary>Starts the heartbeat: a thread that asks the dispatcher to answer every 10 ms and records the longest wait.</summary>
    public void StartHeartbeat()
    {
        _heartbeat = new Thread(() =>
        {
            while (!_stop.IsCancellationRequested)
            {
                var sent = Stopwatch.StartNew();
                try
                {
                    Dispatcher.Invoke(() => { }, DispatcherPriority.Normal, _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                var gap = sent.Elapsed.Ticks;
                var seen = Interlocked.Read(ref _maxGapTicks);
                while (gap > seen && Interlocked.CompareExchange(ref _maxGapTicks, gap, seen) != seen)
                {
                    seen = Interlocked.Read(ref _maxGapTicks);
                }

                if (gap > TimeSpan.FromMilliseconds(100).Ticks)
                {
                    Interlocked.Increment(ref _gapOver100);
                }

                Thread.Sleep(10);
            }
        })
        {
            IsBackground = true,
            Name = "rig heartbeat",
        };
        _heartbeat.Start();
    }

    public void ResetHeartbeat()
    {
        Interlocked.Exchange(ref _maxGapTicks, 0);
        Interlocked.Exchange(ref _gapOver100, 0);
    }

    public void Change(Func<PlayerSettings, PlayerSettings> edit) => Settings.Update(s => s with { Player = edit(s.Player) });

    public void Dispose()
    {
        _stop.Cancel();
        _heartbeat?.Join(2000);
        try
        {
            Dispatcher.Invoke(() =>
            {
                View.Dispose();
                Alarms.Dispose();
                Player.Dispose();
                Loudness.Dispose();
                Library.Dispose();
                Settings.Dispose();
            });
        }
        catch (Exception)
        {
            // A teardown problem must not hide the result of the test.
        }

        _soundCard?.Join(2000);
        Dispatcher.InvokeShutdown();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Cleaned later.
        }
    }

    // The real decoder; the sound card is a thread pulling the mixer.
    private sealed class PumpBackend(UiResponsivenessRig rig) : IPlayerBackend
    {
        private readonly NAudioPlayerBackend _real = new();

        public IPlayerOutput OpenOutput(string? deviceId, Func<int, int, IPcmSource> createSource)
        {
            var output = new CaptureOutput(createSource(48000, 2));
            rig.Output = output;
            rig._soundCard = new Thread(() =>
            {
                var clock = Stopwatch.StartNew();
                long frames = 0;
                while (!rig._stop.IsCancellationRequested && !output.Disposed)
                {
                    var due = (long)(clock.Elapsed.TotalSeconds * 48000);
                    if (due - frames >= 480)
                    {
                        output.Pull(480);
                        frames += 480;
                    }
                    else
                    {
                        Thread.Sleep(2);
                    }
                }
            })
            {
                IsBackground = true,
                Name = "rig sound card",
            };
            rig._soundCard.Start();
            return output;
        }

        public IPcmTrack OpenTrack(string path, int sampleRate, int channels) => _real.OpenTrack(path, sampleRate, channels);

        public TrackProfile? Analyze(string path, int sampleRate, int channels) => _real.Analyze(path, sampleRate, channels);

        public NativePcm? OpenNative(string path) => ((IPlayerBackend)_real).OpenNative(path);
    }
}

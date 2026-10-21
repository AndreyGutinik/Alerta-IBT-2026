using AlertaIBT.App.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.App.Tests.Platform;

public sealed class LegacyAppDetectorTests : IDisposable
{
    private const string ElectronCommand = @"C:\Users\igreja\AppData\Local\Programs\Alerta-IBT\Alerta-IBT.exe --hidden";
    private const string OurCommand = @"""C:\Users\igreja\AppData\Local\AlertaIBT\current\AlertaIBT.exe"" --tray";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-legacy-" + Guid.NewGuid().ToString("N"));

    private string Marker => Path.Combine(_dir, "legacy-notice-shown.txt");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class FakeStore : IRunKeyStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Exception? FailReadWith { get; set; }

        public Exception? FailRemoveWith { get; set; }

        public string? Get(string name) => Values.TryGetValue(name, out var v) ? v : null;

        public void Set(string name, string command) => Values[name] = command;

        public IReadOnlyDictionary<string, string> GetAll() => FailReadWith is not null ? throw FailReadWith : Values;

        public void Remove(string name)
        {
            if (FailRemoveWith is not null)
            {
                throw FailRemoveWith;
            }

            Values.Remove(name);
        }
    }

    [Theory]
    [InlineData("electron.app.Alerta-IBT", ElectronCommand, true)]
    [InlineData("ELECTRON.APP.ALERTA-IBT", "anything", true)]
    [InlineData("Avisos", @"""C:\x\Alerta-IBT.exe"" --hidden", true)]
    [InlineData("Avisos", @"C:\Program Files\Alerta-IBT\ALERTA-IBT.EXE", true)]
    [InlineData("AlertaIBT", OurCommand, false)]
    [InlineData("AlertaIBT", ElectronCommand, false)] // our own value name is never touched
    [InlineData("electron.app.Electron", @"C:\dev\Alerta-IBT\node_modules\electron\dist\electron.exe --hidden", false)] // the developer's dev copy
    [InlineData("Spotify", @"C:\Users\x\Spotify.exe --autostart", false)]
    public void Only_the_old_apps_entries_count(string name, string command, bool expected)
    {
        Assert.Equal(expected, LegacyAppDetector.IsLegacyEntry(name, command));
    }

    [Fact]
    public void Detect_reports_the_running_process_and_the_startup_entries()
    {
        var store = new FakeStore();
        store.Values["electron.app.Alerta-IBT"] = ElectronCommand;
        store.Values["AlertaIBT"] = OurCommand;
        store.Values["Spotify"] = "spotify.exe";

        var findings = new LegacyAppDetector(store, () => true).Detect();

        Assert.True(findings.Running);
        Assert.Equal(["electron.app.Alerta-IBT"], findings.StartupEntries);
        Assert.True(findings.Any);
    }

    [Fact]
    public void Detect_finds_nothing_on_a_clean_pc()
    {
        var store = new FakeStore();
        store.Values["AlertaIBT"] = OurCommand;

        var findings = new LegacyAppDetector(store, () => false).Detect();

        Assert.False(findings.Any);
        Assert.False(LegacyFindings.None.Any);
    }

    [Fact]
    public void Detect_never_throws_when_the_registry_or_the_process_list_cannot_be_read()
    {
        var store = new FakeStore { FailReadWith = new UnauthorizedAccessException("locked") };

        var findings = new LegacyAppDetector(store, () => throw new InvalidOperationException("no process list")).Detect();

        Assert.False(findings.Any);
    }

    [Fact]
    public void Removing_takes_out_only_the_old_entries_and_never_our_own()
    {
        var store = new FakeStore();
        store.Values["electron.app.Alerta-IBT"] = ElectronCommand;
        store.Values["AlertaIBT"] = OurCommand;
        var detector = new LegacyAppDetector(store, () => false);

        var removed = detector.RemoveFromStartup(new LegacyFindings(false, ["electron.app.Alerta-IBT", "AlertaIBT"]));

        Assert.Equal(1, removed);
        Assert.False(store.Values.ContainsKey("electron.app.Alerta-IBT"));
        Assert.Equal(OurCommand, store.Values["AlertaIBT"]);
    }

    [Fact]
    public void A_value_that_changed_since_it_was_found_is_not_removed()
    {
        var store = new FakeStore();
        store.Values["Avisos"] = @"C:\other\program.exe"; // it once pointed at Alerta-IBT.exe; now it is somebody else's
        var detector = new LegacyAppDetector(store, () => false);

        var removed = detector.RemoveFromStartup(new LegacyFindings(false, ["Avisos"]));

        Assert.Equal(0, removed);
        Assert.True(store.Values.ContainsKey("Avisos"));
    }

    [Fact]
    public void A_registry_that_refuses_the_removal_is_survived()
    {
        var store = new FakeStore { FailRemoveWith = new UnauthorizedAccessException("locked") };
        store.Values["electron.app.Alerta-IBT"] = ElectronCommand;

        var removed = new LegacyAppDetector(store, () => false).RemoveFromStartup(new LegacyFindings(false, ["electron.app.Alerta-IBT"]));

        Assert.Equal(0, removed);
    }

    [Fact]
    public void The_real_process_probe_runs_without_throwing()
    {
        _ = LegacyAppDetector.IsOldProcessRunning();
    }

    [Fact]
    public void The_prompt_offers_the_removal_only_when_there_is_an_entry_to_remove()
    {
        var both = LegacyAppNotice.BuildPrompt(new LegacyFindings(true, ["electron.app.Alerta-IBT"]));
        var entryOnly = LegacyAppNotice.BuildPrompt(new LegacyFindings(false, ["electron.app.Alerta-IBT"]));
        var runningOnly = LegacyAppNotice.BuildPrompt(new LegacyFindings(true, []));

        Assert.True(both.OfferRemoval);
        Assert.Contains("aberto e come\U000000E7a junto com o Windows", both.Message, StringComparison.Ordinal);
        Assert.Contains("feche-o antes de testar o novo", both.Message, StringComparison.Ordinal);
        Assert.True(entryOnly.OfferRemoval);
        Assert.Contains("come\U000000E7a junto com o Windows", entryOnly.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("aberto", entryOnly.Message, StringComparison.Ordinal);
        Assert.False(runningOnly.OfferRemoval);
        Assert.Contains("8787", runningOnly.Message, StringComparison.Ordinal);
        Assert.Contains("Telegram", runningOnly.Message, StringComparison.Ordinal);
        Assert.Contains("uma vez", runningOnly.Message, StringComparison.Ordinal);
    }

    private LegacyAppNotice Notice(FakeStore store, bool running, Func<LegacyPrompt, bool> ask) =>
        new(new LegacyAppDetector(store, () => running), Marker, ask, NullLogger.Instance);

    [Fact]
    public void The_warning_is_shown_once_and_a_yes_takes_the_old_entry_out()
    {
        var store = new FakeStore();
        store.Values["electron.app.Alerta-IBT"] = ElectronCommand;
        var asked = new List<LegacyPrompt>();
        var notice = Notice(store, running: true, prompt =>
        {
            asked.Add(prompt);
            return true;
        });

        Assert.True(notice.Run());
        Assert.False(notice.Run()); // never again, even though the old app is still there
        Assert.False(Notice(store, true, _ => true).Run()); // not even after a restart (marker file)

        Assert.Single(asked);
        Assert.Empty(store.Values);
        Assert.True(File.Exists(Marker));
    }

    [Fact]
    public void A_no_keeps_the_entry_and_the_warning_is_still_spent()
    {
        var store = new FakeStore();
        store.Values["electron.app.Alerta-IBT"] = ElectronCommand;

        Assert.True(Notice(store, false, _ => false).Run());

        Assert.True(store.Values.ContainsKey("electron.app.Alerta-IBT"));
        Assert.False(Notice(store, false, _ => throw new InvalidOperationException("asked twice")).Run());
    }

    [Fact]
    public void Nothing_found_means_no_dialog_and_no_marker()
    {
        var store = new FakeStore();
        var notice = Notice(store, running: false, _ => throw new InvalidOperationException("must not ask"));

        Assert.False(notice.Run());
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public void The_marker_is_written_before_the_dialog_so_a_crash_cannot_bring_it_back()
    {
        var store = new FakeStore();
        store.Values["electron.app.Alerta-IBT"] = ElectronCommand;
        var markerExistedWhileAsking = false;
        var notice = Notice(store, false, _ =>
        {
            markerExistedWhileAsking = File.Exists(Marker);
            throw new InvalidOperationException("the dialog crashed");
        });

        Assert.False(notice.Run()); // swallowed: the app goes on
        Assert.True(markerExistedWhileAsking);
        Assert.True(File.Exists(Marker));
    }

    [Fact]
    public void Uninstalling_removes_the_apps_own_startup_value_and_nothing_else()
    {
        var store = new FakeStore();
        store.Values["AlertaIBT"] = OurCommand;
        store.Values["Spotify"] = "spotify.exe";

        Assert.True(StartupRegistration.Remove(store));

        Assert.False(store.Values.ContainsKey("AlertaIBT"));
        Assert.True(store.Values.ContainsKey("Spotify"));
        Assert.True(StartupRegistration.Remove(store)); // already gone: still fine
    }

    [Fact]
    public void Uninstalling_with_a_locked_registry_does_not_throw()
    {
        var store = new FakeStore { FailRemoveWith = new UnauthorizedAccessException("locked") };

        Assert.False(StartupRegistration.Remove(store));
    }
}

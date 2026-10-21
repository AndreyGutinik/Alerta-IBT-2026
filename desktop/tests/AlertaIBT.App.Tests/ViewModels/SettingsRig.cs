using System.Text;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.App.Tests.ViewModels;

/// <summary>A protector that is not DPAPI but hides the text the same way for the tests: reversed and base64, never the clear text.</summary>
internal sealed class ReversingProtector : ISecretProtector
{
    public string? Protect(string? plain)
    {
        if (string.IsNullOrWhiteSpace(plain))
        {
            return null;
        }

        return "P:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(new string(plain.Reverse().ToArray())));
    }

    public string? Unprotect(string? protectedValue)
    {
        if (protectedValue is null || !protectedValue.StartsWith("P:", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return new string(Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[2..])).Reverse().ToArray());
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>Real settings in a temp folder, a fake clock, and the autosave on top: what the settings tabs run on.</summary>
internal sealed class SettingsRig : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-vm-" + Guid.NewGuid().ToString("N"));

    public SettingsRig(AppSettings? initial = null)
    {
        Directory.CreateDirectory(_dir);
        Clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 9, 30, 15, TimeSpan.Zero));
        Store = new SettingsStore(SettingsPath, Clock);
        if (initial is not null)
        {
            Store.Save(initial);
        }

        Settings = new SettingsService(Store, Clock, Errors.Add);
        Hub = new StatusHub();
        Saver = new Autosaver(Settings, clock: Clock);
        Settings.Changed += _ => ChangedCount++;
    }

    public FakeTimeProvider Clock { get; }

    public SettingsStore Store { get; }

    public SettingsService Settings { get; }

    public StatusHub Hub { get; }

    public Autosaver Saver { get; }

    public ReversingProtector Protector { get; } = new();

    public List<Exception> Errors { get; } = [];

    public int ChangedCount { get; private set; }

    public string SettingsPath => Path.Combine(_dir, "settings.json");

    public string Folder => _dir;

    public AppSettings OnDisk() => new SettingsStore(SettingsPath, Clock).Load().Settings;

    public string FileText() => File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : string.Empty;

    public void Rest() => Clock.Advance(Autosaver.Delay);

    public void Dispose()
    {
        Saver.Dispose();
        Settings.Dispose();
        if (Directory.Exists(_dir))
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
                // A scanner may hold a file for a moment; the temp folder is cleaned later.
            }
        }
    }
}

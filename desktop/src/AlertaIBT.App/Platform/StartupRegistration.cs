using System.IO;
using Microsoft.Win32;

namespace AlertaIBT.App.Platform;

/// <summary>Where the "start with Windows" entry lives. A fake stands in for the registry in tests.</summary>
public interface IRunKeyStore
{
    /// <summary>The command stored under <paramref name="name"/>, or null when there is none.</summary>
    string? Get(string name);

    /// <summary>Stores <paramref name="command"/> under <paramref name="name"/>, replacing what was there.</summary>
    void Set(string name, string command);

    /// <summary>Every value of the key as name and command (used to find the old Electron app's entry).</summary>
    IReadOnlyDictionary<string, string> GetAll();

    /// <summary>Deletes the value <paramref name="name"/>; nothing happens when it does not exist.</summary>
    void Remove(string name);
}

/// <summary>The per-user <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> key.</summary>
public sealed class WindowsRunKeyStore : IRunKeyStore
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <inheritdoc />
    public string? Get(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(name) as string;
    }

    /// <inheritdoc />
    public void Set(string name, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(name, command, RegistryValueKind.String);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetAll()
    {
        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        if (key is null)
        {
            return all;
        }

        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name) is string command)
            {
                all[name] = command;
            }
        }

        return all;
    }

    /// <inheritdoc />
    public void Remove(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>What <see cref="StartupRegistration.Apply"/> did.</summary>
public enum StartupRegistrationResult
{
    /// <summary>The entry was written (new, or it pointed somewhere else).</summary>
    Written,

    /// <summary>The entry was already exactly right; nothing was touched.</summary>
    AlreadyCorrect,

    /// <summary>Registration is off for this build (a development build must not register its own folder).</summary>
    Disabled,

    /// <summary>The running program's path is unknown, so there is nothing to register.</summary>
    NoExecutablePath,

    /// <summary>The registry refused the write; the app goes on without it.</summary>
    Failed,
}

/// <summary>
/// "Start with Windows", always on and with no switch in the interface (spec 7.3): the value <c>AlertaIBT</c> in the user's
/// Run key holds <c>"&lt;exe&gt;" --tray</c>, so the app opens straight into the tray. It is applied on every start, which
/// also repairs the entry after a reinstall moves the program.
/// </summary>
public sealed class StartupRegistration
{
    /// <summary>The name of the Run value.</summary>
    public const string ValueName = "AlertaIBT";

    private readonly IRunKeyStore _store;
    private readonly string? _executablePath;
    private readonly bool _enabled;

    /// <param name="store">The Run key.</param>
    /// <param name="executablePath">The running program, normally <see cref="Environment.ProcessPath"/>.</param>
    /// <param name="enabled">False in development builds, which must not register their build folder.</param>
    public StartupRegistration(IRunKeyStore store, string? executablePath, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _executablePath = executablePath;
        _enabled = enabled;
    }

    /// <summary>The command the Run value holds for <paramref name="executablePath"/>: the quoted path and <c>--tray</c>.</summary>
    public static string BuildCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        return $"\"{executablePath}\" --tray";
    }

    /// <summary>
    /// Takes the app out of "start with Windows". Used by the uninstaller (a Velopack hook that must be quick and must not
    /// throw), so that removing the program leaves no entry behind. Never throws.
    /// </summary>
    public static bool Remove(IRunKeyStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        try
        {
            store.Remove(ValueName);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    /// <summary>Makes the Run value right. Never throws: a locked-down registry only means no autostart.</summary>
    public StartupRegistrationResult Apply(Action<Exception>? onError = null)
    {
        if (!_enabled)
        {
            return StartupRegistrationResult.Disabled;
        }

        if (string.IsNullOrWhiteSpace(_executablePath))
        {
            return StartupRegistrationResult.NoExecutablePath;
        }

        var command = BuildCommand(_executablePath);
        try
        {
            if (string.Equals(_store.Get(ValueName), command, StringComparison.Ordinal))
            {
                return StartupRegistrationResult.AlreadyCorrect;
            }

            _store.Set(ValueName, command);
            return StartupRegistrationResult.Written;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            onError?.Invoke(ex);
            return StartupRegistrationResult.Failed;
        }
    }
}

using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.App.Platform;

/// <summary>What was found of the old Electron app (version 7).</summary>
/// <param name="Running">The old program (<c>Alerta-IBT.exe</c>) is running now.</param>
/// <param name="StartupEntries">Names of the "start with Windows" values that launch it.</param>
public sealed record LegacyFindings(bool Running, IReadOnlyList<string> StartupEntries)
{
    /// <summary>Nothing of the old app was found.</summary>
    public static LegacyFindings None { get; } = new(false, []);

    /// <summary>The old app is running or starts with Windows.</summary>
    public bool Any => Running || StartupEntries.Count > 0;
}

/// <summary>
/// Finds the old Electron app: a running <c>Alerta-IBT.exe</c> or a value in the user's Run key that starts it (electron-builder
/// names it <c>electron.app.Alerta-IBT</c> and points at <c>Alerta-IBT.exe</c>). The two apps together read the same Telegram
/// bot twice and fight for port 8787 (spec 7.3), so the new one tells the owner once and offers to take the old one out of the
/// Windows startup. It never closes or uninstalls anything.
/// </summary>
public sealed class LegacyAppDetector
{
    /// <summary>The process name of the old app (without <c>.exe</c>).</summary>
    public const string ProcessName = "Alerta-IBT";

    /// <summary>The Run value electron-builder's <c>app.setLoginItemSettings</c> writes for the old app.</summary>
    public const string ElectronRunValue = "electron.app.Alerta-IBT";

    private const string ExecutableName = "Alerta-IBT.exe";

    private readonly IRunKeyStore _store;
    private readonly Func<bool> _isProcessRunning;

    /// <param name="store">The user's Run key.</param>
    /// <param name="isProcessRunning">Whether the old program is running; <see cref="IsOldProcessRunning"/> in the app.</param>
    public LegacyAppDetector(IRunKeyStore store, Func<bool> isProcessRunning)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(isProcessRunning);

        _store = store;
        _isProcessRunning = isProcessRunning;
    }

    /// <summary>Whether a Run value belongs to the old app. The new app's own <c>AlertaIBT</c> value never matches.</summary>
    public static bool IsLegacyEntry(string name, string command)
    {
        if (string.Equals(name, StartupRegistration.ValueName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(name, ElectronRunValue, StringComparison.OrdinalIgnoreCase)
            || (command?.Contains(ExecutableName, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>The real check: any process named <c>Alerta-IBT</c>.</summary>
    public static bool IsOldProcessRunning()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>Looks for the old app. Never throws: a registry or process list that cannot be read counts as "not found".</summary>
    public LegacyFindings Detect()
    {
        var running = false;
        try
        {
            running = _isProcessRunning();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            // An unreadable process list is not a reason to stop the app.
        }

        var entries = new List<string>();
        try
        {
            foreach (var (name, command) in _store.GetAll())
            {
                if (IsLegacyEntry(name, command))
                {
                    entries.Add(name);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Same: no entry found.
        }

        return new LegacyFindings(running, entries);
    }

    /// <summary>Deletes the old app's Run values. Returns how many were removed; never throws.</summary>
    public int RemoveFromStartup(LegacyFindings findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var removed = 0;
        foreach (var name in findings.StartupEntries)
        {
            try
            {
                // Only a value that is still the old app's right now (it may have changed since Detect); never our own.
                if (_store.GetAll().TryGetValue(name, out var command) && IsLegacyEntry(name, command))
                {
                    _store.Remove(name);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                // Leave this one; the others may still go.
            }
        }

        return removed;
    }
}

/// <summary>The words of the one-time warning.</summary>
/// <param name="Title">Window title.</param>
/// <param name="Message">What the owner reads.</param>
/// <param name="OfferRemoval">Yes/No question (take the old app out of the Windows startup); false = only information.</param>
public sealed record LegacyPrompt(string Title, string Message, bool OfferRemoval);

/// <summary>
/// The one-time warning about the old app (spec 7.3): when it is found, a dialog explains the clash and offers to remove the
/// old app from the Windows startup. It is shown at most once ever (a marker file in the data folder), because the status
/// lights already keep saying "Porta ocupada" / "Conflito" while both run. Never throws.
/// </summary>
public sealed class LegacyAppNotice
{
    private readonly LegacyAppDetector _detector;
    private readonly string _markerFile;
    private readonly Func<LegacyPrompt, bool> _ask;
    private readonly ILogger _logger;

    /// <param name="detector">Finds the old app.</param>
    /// <param name="markerFile">A file whose existence means "already warned".</param>
    /// <param name="ask">Shows the prompt; returns true when the owner chose to remove the old app from the startup.</param>
    /// <param name="logger">Where the outcome is logged.</param>
    public LegacyAppNotice(LegacyAppDetector detector, string markerFile, Func<LegacyPrompt, bool> ask, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentException.ThrowIfNullOrWhiteSpace(markerFile);
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(logger);

        _detector = detector;
        _markerFile = markerFile;
        _ask = ask;
        _logger = logger;
    }

    /// <summary>The words for what was found.</summary>
    public static LegacyPrompt BuildPrompt(LegacyFindings findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var offer = findings.StartupEntries.Count > 0;
        var what = (findings.Running, offer) switch
        {
            (true, true) => "est\U000000E1 aberto e come\U000000E7a junto com o Windows",
            (true, false) => "est\U000000E1 aberto",
            _ => "come\U000000E7a junto com o Windows",
        };

        var message = $"O Alerta-IBT antigo (vers\U000000E3o 7) {what}.\n\n"
            + "Os dois juntos recebem cada mensagem do Telegram em dobro e brigam pela porta 8787 (o aviso da prega\U000000E7\U000000E3o do Holyrics).\n\n"
            + (offer
                ? "Quer tirar o antigo do in\U000000EDcio do Windows? Ele n\U000000E3o \U000000E9 fechado nem desinstalado agora"
                    + (findings.Running ? "; feche-o antes de testar o novo." : ".")
                : "Feche o antigo para o novo assumir o Telegram e a porta 8787.")
            + "\n\nEste aviso aparece uma vez s\U000000F3.";
        return new LegacyPrompt("Alerta-IBT antigo encontrado", message, offer);
    }

    /// <summary>Warns once if the old app is there. Returns true when the prompt was shown.</summary>
    public bool Run()
    {
        try
        {
            if (File.Exists(_markerFile))
            {
                return false;
            }

            var findings = _detector.Detect();
            if (!findings.Any)
            {
                return false;
            }

            // Mark first: a crash or a kill while the dialog is open must not bring it back at every start.
            Directory.CreateDirectory(Path.GetDirectoryName(_markerFile)!);
            File.WriteAllText(_markerFile, DateTimeOffset.Now.ToString("O"));

            var remove = _ask(BuildPrompt(findings));
            var removed = remove ? _detector.RemoveFromStartup(findings) : 0;
            _logger.LogInformation(
                "Alerta-IBT antigo encontrado (aberto: {Running}; entradas no inicio do Windows: {Entries}); retirado do inicio: {Removed}",
                findings.Running,
                findings.StartupEntries.Count,
                removed);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nao foi possivel verificar o Alerta-IBT antigo");
            return false;
        }
    }
}

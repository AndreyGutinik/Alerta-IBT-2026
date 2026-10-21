using System.Collections.ObjectModel;
using AlertaIBT.Core.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.App.ViewModels;

/// <summary>
/// The Diagnóstico area of the Configurações tab (spec 7.2): folded away by default; opened, it shows the newest log entries
/// (colored by level) with "Copiar logs", "Exportar" and "Limpar". The window asks it to look at the files again while it is open.
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject, IDisposable
{
    /// <summary>How many entries the view keeps.</summary>
    public const int MaxEntries = 300;

    private readonly LogFiles _logs;
    private readonly Action<string> _copy;
    private readonly Func<string?> _pickExportPath;
    private readonly ILogger _logger;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _meta = "sem registros";

    [ObservableProperty]
    private string _message = string.Empty;

    /// <param name="logs">The log files.</param>
    /// <param name="copy">Puts text on the clipboard.</param>
    /// <param name="pickExportPath">Asks the user where to save the export; null when they cancel.</param>
    /// <param name="logger">Receives failures.</param>
    public DiagnosticsViewModel(LogFiles logs, Action<string> copy, Func<string?> pickExportPath, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(logs);
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(pickExportPath);

        _logs = logs;
        _copy = copy;
        _pickExportPath = pickExportPath;
        _logger = logger ?? NullLogger.Instance;
        CopyCommand = new RelayCommand(Copy);
        ExportCommand = new RelayCommand(Export);
        ClearCommand = new RelayCommand(Clear);
        RefreshCommand = new RelayCommand(Refresh);
    }

    /// <summary>The newest entries, newest first.</summary>
    public ObservableCollection<LogEntry> Entries { get; } = [];

    /// <summary>"Copiar logs".</summary>
    public IRelayCommand CopyCommand { get; }

    /// <summary>"Exportar".</summary>
    public IRelayCommand ExportCommand { get; }

    /// <summary>"Limpar".</summary>
    public IRelayCommand ClearCommand { get; }

    /// <summary>Looks at the files again.</summary>
    public IRelayCommand RefreshCommand { get; }

    /// <summary>Whether the short message is shown.</summary>
    public bool HasMessage => Message.Length > 0;

    /// <summary>Reads the newest entries from the files (does nothing while the area is folded away).</summary>
    public void Refresh()
    {
        if (!IsOpen)
        {
            return;
        }

        try
        {
            Load();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao ler os logs");
            Message = "N\U000000E3o foi poss\U000000EDvel ler os logs.";
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Entries.Clear();
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (value)
        {
            Message = string.Empty;
            Refresh();
        }
        else
        {
            Entries.Clear(); // folded away: nothing to keep in memory
        }
    }

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));

    private void Load()
    {
        var newest = _logs.Newest(MaxEntries);
        var errors = newest.Count(e => e.Severity == LogSeverity.Error);

        // Same entries as before: leave the list alone so the scroll position and any selection stay.
        if (!SameAs(newest))
        {
            Entries.Clear();
            foreach (var entry in newest)
            {
                Entries.Add(entry);
            }
        }

        Meta = newest.Count == 0
            ? "sem registros"
            : $"{newest.Count} registros \U000000B7 " + (errors == 0 ? "sem erros" : $"{errors} erro(s)");
    }

    private bool SameAs(IReadOnlyList<LogEntry> newest)
    {
        if (newest.Count != Entries.Count)
        {
            return false;
        }

        for (var i = 0; i < newest.Count; i++)
        {
            if (!ReferenceEquals(newest[i], Entries[i]) && newest[i] != Entries[i])
            {
                return false;
            }
        }

        return true;
    }

    private void Copy()
    {
        try
        {
            var text = _logs.AllText();
            if (text.Length == 0)
            {
                Message = "N\U000000E3o h\U000000E1 logs para copiar.";
                return;
            }

            _copy(text);
            Message = "Logs copiados.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao copiar os logs");
            Message = "N\U000000E3o foi poss\U000000EDvel copiar.";
        }
    }

    private void Export()
    {
        try
        {
            var path = _pickExportPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            _logs.ExportTo(path);
            Message = "Logs exportados.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao exportar os logs");
            Message = "N\U000000E3o foi poss\U000000EDvel exportar.";
        }
    }

    private void Clear()
    {
        try
        {
            _logs.Clear();
            Message = "Logs limpos.";
            Refresh();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao limpar os logs");
            Message = "N\U000000E3o foi poss\U000000EDvel limpar.";
        }
    }
}

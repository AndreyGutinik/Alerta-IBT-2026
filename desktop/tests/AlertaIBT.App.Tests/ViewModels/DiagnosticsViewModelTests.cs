using System.Text;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Diagnostics;

namespace AlertaIBT.App.Tests.ViewModels;

public sealed class DiagnosticsViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-diag-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _copied = [];
    private string? _exportPath;
    private readonly DiagnosticsViewModel _vm;

    public DiagnosticsViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _vm = new DiagnosticsViewModel(new LogFiles(_dir), _copied.Add, () => _exportPath);
    }

    public void Dispose()
    {
        _vm.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private void WriteLog(params string[] lines) =>
        File.WriteAllText(Path.Combine(_dir, "alertaibt-20261020.log"), string.Join("\n", lines) + "\n", new UTF8Encoding(false));

    private static string Line(string time, string level, string message) => $"2026-10-20 {time} [{level}] Test: {message}";

    [Fact]
    public void It_is_folded_away_at_first_and_reads_nothing()
    {
        WriteLog(Line("08:00:00.000", "INF", "iniciado"));

        Assert.False(_vm.IsOpen);
        Assert.Empty(_vm.Entries);
    }

    [Fact]
    public void Opening_shows_the_logs_newest_first_with_the_counts()
    {
        WriteLog(Line("08:00:00.000", "INF", "iniciado"), Line("08:00:01.000", "ERR", "falhou"), Line("08:00:02.000", "INF", "voltou"));

        _vm.IsOpen = true;

        Assert.Equal(3, _vm.Entries.Count);
        Assert.Contains("voltou", _vm.Entries[0].Text);
        Assert.Equal(LogSeverity.Error, _vm.Entries[1].Severity);
        Assert.Equal("3 registros \U000000B7 1 erro(s)", _vm.Meta);
    }

    [Fact]
    public void No_errors_says_sem_erros_and_no_logs_says_sem_registros()
    {
        WriteLog(Line("08:00:00.000", "INF", "iniciado"));
        _vm.IsOpen = true;
        Assert.Equal("1 registros \U000000B7 sem erros", _vm.Meta);

        File.Delete(Path.Combine(_dir, "alertaibt-20261020.log"));
        _vm.Refresh();

        Assert.Equal("sem registros", _vm.Meta);
        Assert.Empty(_vm.Entries);
    }

    [Fact]
    public void Refresh_picks_up_new_entries_and_leaves_the_list_alone_when_nothing_changed()
    {
        WriteLog(Line("08:00:00.000", "INF", "um"));
        _vm.IsOpen = true;
        var first = _vm.Entries[0];
        var changes = 0;
        _vm.Entries.CollectionChanged += (_, _) => changes++;

        _vm.Refresh();
        Assert.Equal(0, changes);

        WriteLog(Line("08:00:00.000", "INF", "um"), Line("08:00:05.000", "WRN", "dois"));
        _vm.Refresh();

        Assert.Equal(2, _vm.Entries.Count);
        Assert.Contains("dois", _vm.Entries[0].Text);
        Assert.Equal(first, _vm.Entries[1]);
    }

    [Fact]
    public void Folding_it_away_again_frees_the_list()
    {
        WriteLog(Line("08:00:00.000", "INF", "um"));
        _vm.IsOpen = true;

        _vm.IsOpen = false;

        Assert.Empty(_vm.Entries);
    }

    [Fact]
    public void Copiar_puts_the_whole_log_text_on_the_clipboard()
    {
        WriteLog(Line("08:00:00.000", "INF", "um"), Line("08:00:01.000", "INF", "dois"));

        _vm.CopyCommand.Execute(null);

        Assert.Single(_copied);
        Assert.Contains("um", _copied[0]);
        Assert.Contains("dois", _copied[0]);
        Assert.Equal("Logs copiados.", _vm.Message);
        Assert.True(_vm.HasMessage);
    }

    [Fact]
    public void Copiar_without_logs_says_so_and_copies_nothing()
    {
        _vm.CopyCommand.Execute(null);

        Assert.Empty(_copied);
        Assert.Contains("N\U000000E3o h\U000000E1 logs", _vm.Message);
    }

    [Fact]
    public void Exportar_writes_the_file_the_user_picked_and_does_nothing_when_they_cancel()
    {
        WriteLog(Line("08:00:00.000", "INF", "um"));

        _vm.ExportCommand.Execute(null);
        Assert.Equal(string.Empty, _vm.Message);

        _exportPath = Path.Combine(_dir, "saida.txt");
        _vm.ExportCommand.Execute(null);

        Assert.Contains("um", File.ReadAllText(_exportPath));
        Assert.Equal("Logs exportados.", _vm.Message);
    }

    [Fact]
    public void Exportar_to_a_place_that_cannot_be_written_says_so_instead_of_crashing()
    {
        WriteLog(Line("08:00:00.000", "INF", "um"));
        _exportPath = Path.Combine(_dir, "nao-existe", "saida.txt");

        _vm.ExportCommand.Execute(null);

        Assert.Contains("N\U000000E3o foi poss\U000000EDvel exportar", _vm.Message);
    }

    [Fact]
    public void Limpar_empties_the_logs_and_the_list()
    {
        WriteLog(Line("08:00:00.000", "INF", "um"));
        _vm.IsOpen = true;
        Assert.Single(_vm.Entries);

        _vm.ClearCommand.Execute(null);

        Assert.Empty(_vm.Entries);
        Assert.Equal("Logs limpos.", _vm.Message);
        Assert.Equal("sem registros", _vm.Meta);
        Assert.All(Directory.GetFiles(_dir, "alertaibt-*.log"), f => Assert.Equal(0, new FileInfo(f).Length));
    }
}

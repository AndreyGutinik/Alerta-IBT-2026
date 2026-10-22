using System.Collections.ObjectModel;
using AlertaIBT.App.Services;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.App.ViewModels;

/// <summary>One waiting alert in the queue list: where it came from, its text, and the buttons to move or remove it.</summary>
public sealed class QueueRowViewModel
{
    internal QueueRowViewModel(QueueItem item, int index, int count, Action<string, int> move, Action<string> remove)
    {
        Id = item.Id;
        Text = item.Text;
        SourceName = HomeViewModel.SourceName(item.Source);
        Urgent = item.Urgent;
        CorrectionTip = HomeViewModel.CorrectionTip(item);
        CanMoveUp = index > 0;
        CanMoveDown = index < count - 1;
        MoveUpCommand = new RelayCommand(() => move(Id, -1), () => CanMoveUp);
        MoveDownCommand = new RelayCommand(() => move(Id, 1), () => CanMoveDown);
        RemoveCommand = new RelayCommand(() => remove(Id));
    }

    /// <summary>The alert's id.</summary>
    public string Id { get; }

    /// <summary>What the bar will show.</summary>
    public string Text { get; }

    /// <summary>TELEGRAM, SITE or PC.</summary>
    public string SourceName { get; }

    /// <summary>Whether it was sent as urgent.</summary>
    public bool Urgent { get; }

    /// <summary>The tooltip of the wand ("Corrigido. Original: ..."), or null when the words were not changed.</summary>
    public string? CorrectionTip { get; }

    /// <summary>Whether the wand is shown.</summary>
    public bool WasCorrected => CorrectionTip is not null;

    /// <summary>Whether there is a place above.</summary>
    public bool CanMoveUp { get; }

    /// <summary>Whether there is a place below.</summary>
    public bool CanMoveDown { get; }

    /// <summary>The up arrow.</summary>
    public IRelayCommand MoveUpCommand { get; }

    /// <summary>The down arrow.</summary>
    public IRelayCommand MoveDownCommand { get; }

    /// <summary>The cross: removes it from the queue.</summary>
    public IRelayCommand RemoveCommand { get; }
}

/// <summary>One line of the history: an alert that was shown (green check), refused (red X with why), cancelled or expired.</summary>
public sealed class HistoryRowViewModel
{
    internal HistoryRowViewModel(HistoryEntry entry, Action<QueueItem> replay)
    {
        Entry = entry;
        Text = entry.Item.Text;
        SourceName = HomeViewModel.SourceName(entry.Item.Source);
        CorrectionTip = HomeViewModel.CorrectionTip(entry.Item);
        Reason = ReasonOf(entry);
        (IconKey, Tone) = entry.State switch
        {
            HistoryState.Shown => ("LucideCircleCheck", HistoryTone.Ok),
            HistoryState.Rejected => ("LucideCircleX", HistoryTone.Bad),
            HistoryState.Expired => ("LucideClock", HistoryTone.Muted),
            _ => ("LucideBan", HistoryTone.Muted),
        };
        IconTip = entry.State switch
        {
            HistoryState.Shown => "Exibido no tel\U000000E3o",
            HistoryState.Rejected => "Recusado" + (entry.Reason is { Length: > 0 } why ? ": " + why : string.Empty),
            HistoryState.Expired => "Expirou sem passar no tel\U000000E3o",
            _ => "Cancelado" + (entry.Reason is { Length: > 0 } why2 ? ": " + why2 : string.Empty),
        };
        Time = entry.At.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        CanReplay = entry.State == HistoryState.Shown;
        ReplayCommand = new RelayCommand(() => replay(entry.Item), () => CanReplay);
    }

    /// <summary>The history entry.</summary>
    public HistoryEntry Entry { get; }

    /// <summary>The text of the alert (what was typed, for a refused one that never got a shape).</summary>
    public string Text { get; }

    /// <summary>TELEGRAM, SITE or PC.</summary>
    public string SourceName { get; }

    /// <summary>The wand tooltip, or null when the words were not changed.</summary>
    public string? CorrectionTip { get; }

    /// <summary>Whether the wand is shown.</summary>
    public bool WasCorrected => CorrectionTip is not null;

    /// <summary>The short reason shown at the right (<c>faltou placa</c>, <c>pregação</c>, <c>expirou</c>); empty for a shown alert.</summary>
    public string Reason { get; }

    /// <summary>The reason in capitals, as the history prints it.</summary>
    public string ReasonLabel => Reason.ToUpper(System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));

    /// <summary>Whether the reason is shown.</summary>
    public bool HasReason => Reason.Length > 0;

    /// <summary>The resource key of the state icon.</summary>
    public string IconKey { get; }

    /// <summary>The color family of the state icon and the reason.</summary>
    public HistoryTone Tone { get; }

    /// <summary>The tooltip of the state icon.</summary>
    public string IconTip { get; }

    /// <summary>When it ended, <c>HH:mm</c>.</summary>
    public string Time { get; }

    /// <summary>Whether "Exibir de novo" is offered (only for alerts that were shown).</summary>
    public bool CanReplay { get; }

    /// <summary>The rotate-left arrow: queues the alert again.</summary>
    public IRelayCommand ReplayCommand { get; }

    private static string ReasonOf(HistoryEntry entry) => entry.State switch
    {
        HistoryState.Shown => string.Empty,
        HistoryState.Expired => entry.Reason is { Length: > 0 } expired ? expired : "expirou",
        _ => entry.Reason ?? string.Empty,
    };
}

/// <summary>The color of a history line's icon and reason.</summary>
public enum HistoryTone
{
    /// <summary>Shown: green.</summary>
    Ok,

    /// <summary>Refused: red.</summary>
    Bad,

    /// <summary>Cancelled or expired: grey.</summary>
    Muted,
}

/// <summary>
/// The logic of the Início tab (spec 7.2): the alert on the bar with "Corrigido. Original: ...", Próxima / Encerrar atual, the
/// pause button, the message field with its preview (the quick panel's own view-model, <see cref="Composer"/>, so the field
/// behaves the same in both places), the queue with ↑ ↓ ✕ and "clear", and the history of 50 with "Exibir de novo".
/// </summary>
/// <remarks>
/// Everything that reaches the lists comes from the queue's <c>Changed</c> event, which may be raised on any thread; the lists are
/// rebuilt through <c>post</c> (the UI thread). Clearing the queue asks nothing (spec 5.5).
/// </remarks>
public sealed partial class HomeViewModel : ObservableObject, IDisposable
{
    /// <summary>How long the short message about a button stays.</summary>
    public static readonly TimeSpan NoticeTime = TimeSpan.FromSeconds(4);

    /// <summary>How long the result line of "Limpar historico" stays (it has more to say than the other short messages).</summary>
    public static readonly TimeSpan HistoryNoticeTime = TimeSpan.FromSeconds(7);

    private readonly IAlertGateway _gateway;
    private readonly Action<Action> _post;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private ITimer? _noticeTimer;
    private ITimer? _historyNoticeTimer;
    private bool _disposed;

    [ObservableProperty]
    private string _originalLine = string.Empty;

    [ObservableProperty]
    private string _noticeText = string.Empty;

    [ObservableProperty]
    private FeedbackTone _noticeTone;

    [ObservableProperty]
    private string _queueTitle = "FILA";

    [ObservableProperty]
    private bool _queueIsEmpty = true;

    [ObservableProperty]
    private bool _historyIsEmpty = true;

    [ObservableProperty]
    private string _historyNoticeText = string.Empty;

    [ObservableProperty]
    private FeedbackTone _historyNoticeTone;

    /// <param name="gateway">The alert service as the operator uses it.</param>
    /// <param name="composer">The message field with its preview, send buttons and pause button (the quick panel's view-model).</param>
    /// <param name="post">Runs an action on the UI thread; runs it at once when null (tests).</param>
    /// <param name="clock">Times the short messages.</param>
    /// <param name="logger">Receives failures.</param>
    public HomeViewModel(
        IAlertGateway gateway,
        QuickPanelViewModel composer,
        Action<Action>? post = null,
        TimeProvider? clock = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(composer);

        _gateway = gateway;
        Composer = composer;
        _post = post ?? (action => action());
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;

        SkipCommand = new RelayCommand(Skip);
        ClearQueueCommand = new RelayCommand(ClearQueue);
        ClearHistoryCommand = new AsyncRelayCommand(ClearHistoryAsync);

        _gateway.Queue.Changed += OnQueueChanged;
        Refresh();
    }

    /// <summary>The message field, preview, Enviar / Enviar urgente / Limpar and the pause button.</summary>
    public QuickPanelViewModel Composer { get; }

    /// <summary>The waiting alerts, next to show first.</summary>
    public ObservableCollection<QueueRowViewModel> Queue { get; } = [];

    /// <summary>The last 50 alerts that ended, newest first.</summary>
    public ObservableCollection<HistoryRowViewModel> History { get; } = [];

    /// <summary>"Próxima" and "Encerrar atual": both end the alert on the bar now.</summary>
    public IRelayCommand SkipCommand { get; }

    /// <summary>The trash button of the queue: removes every waiting alert, asking nothing.</summary>
    public IRelayCommand ClearQueueCommand { get; }

    /// <summary>
    /// The "Limpar historico" button of the history: clears the history of the PC, the site and the Telegram group, asking
    /// nothing, and tells what it did in <see cref="HistoryNoticeText"/> for a few seconds. Not offered again while it runs.
    /// </summary>
    public IAsyncRelayCommand ClearHistoryCommand { get; }

    /// <summary>Whether the result line of the history is shown.</summary>
    public bool HasHistoryNotice => HistoryNoticeText.Length > 0;

    /// <summary>Whether the "Corrigido. Original: ..." line is shown.</summary>
    public bool HasOriginal => OriginalLine.Length > 0;

    /// <summary>Whether the short message is shown.</summary>
    public bool HasNotice => NoticeText.Length > 0;

    /// <summary>The window was shown: look at everything again.</summary>
    public void OnShown()
    {
        Refresh();
        Composer.OnShown();
    }

    /// <summary>Moves a waiting alert (the arrows of a queue row).</summary>
    public void Move(string id, int delta)
    {
        try
        {
            _gateway.Move(id, delta);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao mover um aviso da fila");
        }
    }

    /// <summary>Removes a waiting alert (the cross of a queue row).</summary>
    public void Remove(string id)
    {
        try
        {
            _gateway.Remove(id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao remover um aviso da fila");
        }
    }

    /// <summary>"Exibir de novo": queues a shown alert again, as the operator.</summary>
    public void Replay(QueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        try
        {
            var outcome = _gateway.Replay(item);
            if (outcome.Kind == SubmitKind.PausedRefused)
            {
                SetNotice("Prega\U000000E7\U000000E3o em andamento: o aviso n\U000000E3o foi enviado.", FeedbackTone.Warn);
            }
            else
            {
                SetNotice(outcome.Position > 0 ? $"Na fila: {outcome.Position}\U000000BA" : "Na fila", FeedbackTone.Ok);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao exibir um aviso de novo");
            SetNotice("N\U000000E3o foi poss\U000000EDvel enviar. Veja o registro (log).", FeedbackTone.Error);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _noticeTimer?.Dispose();
            _historyNoticeTimer?.Dispose();
        }

        _gateway.Queue.Changed -= OnQueueChanged;
    }

    /// <summary>TELEGRAM, SITE or PC.</summary>
    internal static string SourceName(AlertSource source) => source switch
    {
        AlertSource.Telegram => "TELEGRAM",
        AlertSource.Site => "SITE",
        _ => "PC",
    };

    /// <summary>The tooltip of the wand, or null when the correction left the words as they were.</summary>
    internal static string? CorrectionTip(QueueItem item) =>
        item.Corrected && !string.IsNullOrWhiteSpace(item.Original)
            ? "Corrigido\nOriginal: " + item.Original
            : null;

    partial void OnOriginalLineChanged(string value) => OnPropertyChanged(nameof(HasOriginal));

    partial void OnNoticeTextChanged(string value) => OnPropertyChanged(nameof(HasNotice));

    partial void OnHistoryNoticeTextChanged(string value) => OnPropertyChanged(nameof(HasHistoryNotice));

    /// <summary>The line that tells what "Limpar historico" did, and how it is colored (green; yellow when something stays).</summary>
    internal static (string Text, FeedbackTone Tone) HistoryClearMessage(HistoryClearResult result)
    {
        var parts = new List<string> { $"{result.PcEntries} no PC" };
        if (result.Site == SiteClearOutcome.Cleared)
        {
            parts.Add($"{result.SiteRequests} no site");
        }

        parts.Add($"{result.TelegramDeleted} no Telegram");
        var text = "Hist\U000000F3rico limpo: " + string.Join(", ", parts);
        var tone = FeedbackTone.Ok;
        if (result.Site == SiteClearOutcome.Pending)
        {
            text += "; o site ser\U000000E1 limpo quando voltar";
            tone = FeedbackTone.Warn;
        }

        text += ".";
        if (result.TelegramFailed > 0)
        {
            text += result.TelegramFailed == 1
                ? " 1 mensagem do Telegram ficou (mais de 48 h ou sem permiss\U000000E3o)."
                : $" {result.TelegramFailed} mensagens do Telegram ficaram (mais de 48 h ou sem permiss\U000000E3o).";
            tone = FeedbackTone.Warn;
        }

        return (text, tone);
    }

    private async Task ClearHistoryAsync()
    {
        try
        {
            var result = await _gateway.ClearHistoryAsync(CancellationToken.None);
            var (text, tone) = HistoryClearMessage(result);
            SetHistoryNotice(text, tone);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao limpar o historico");
            SetHistoryNotice("N\U000000E3o foi poss\U000000EDvel limpar o hist\U000000F3rico. Veja o registro (log).", FeedbackTone.Error);
        }
    }

    private void SetHistoryNotice(string text, FeedbackTone tone)
    {
        HistoryNoticeText = text;
        HistoryNoticeTone = tone;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _historyNoticeTimer?.Dispose();
            _historyNoticeTimer = _clock.CreateTimer(_ => _post(ClearHistoryNotice), null, HistoryNoticeTime, Timeout.InfiniteTimeSpan);
        }
    }

    private void ClearHistoryNotice()
    {
        HistoryNoticeText = string.Empty;
        HistoryNoticeTone = FeedbackTone.None;
    }

    private void Skip()
    {
        try
        {
            _gateway.Skip();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao encerrar o aviso atual");
        }
    }

    private void ClearQueue()
    {
        try
        {
            _gateway.ClearQueue();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao limpar a fila");
        }
    }

    private void OnQueueChanged(object? sender, EventArgs e) => _post(Refresh);

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var current = _gateway.Queue.Current;
        var pending = _gateway.Queue.Pending;
        var history = _gateway.Queue.History;

        OriginalLine = current is { Corrected: true, Original: { Length: > 0 } original }
            ? "Corrigido. Original: " + original
            : string.Empty;

        Queue.Clear();
        for (var i = 0; i < pending.Count; i++)
        {
            Queue.Add(new QueueRowViewModel(pending[i], i, pending.Count, Move, Remove));
        }

        QueueTitle = pending.Count > 0 ? $"FILA ({pending.Count})" : "FILA";
        QueueIsEmpty = pending.Count == 0;

        History.Clear();
        foreach (var entry in history)
        {
            History.Add(new HistoryRowViewModel(entry, Replay));
        }

        HistoryIsEmpty = history.Count == 0;
    }

    private void SetNotice(string text, FeedbackTone tone)
    {
        NoticeText = text;
        NoticeTone = tone;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _noticeTimer?.Dispose();
            _noticeTimer = _clock.CreateTimer(_ => _post(ClearNotice), null, NoticeTime, Timeout.InfiniteTimeSpan);
        }
    }

    private void ClearNotice()
    {
        NoticeText = string.Empty;
        NoticeTone = FeedbackTone.None;
    }
}

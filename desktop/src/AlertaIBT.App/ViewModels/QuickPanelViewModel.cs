using AlertaIBT.App.Platform;
using AlertaIBT.App.Services;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.App.ViewModels;

/// <summary>One of the three small status icons of the panel (Telegram, Site, LED) with its dot.</summary>
/// <param name="Label">The name, for the tooltip and screen readers.</param>
/// <param name="IconKey">The resource key of the Lucide geometry.</param>
/// <param name="Light">The color of the dot.</param>
/// <param name="Tip">The tooltip: the state, then the detail.</param>
public sealed record StatusChip(string Label, string IconKey, StatusLight Light, string Tip);

/// <summary>How the short message under the field is colored.</summary>
public enum FeedbackTone
{
    /// <summary>No message.</summary>
    None,

    /// <summary>It worked.</summary>
    Ok,

    /// <summary>It did not go, for a reason that is nobody's fault (the sermon pause).</summary>
    Warn,

    /// <summary>Something failed.</summary>
    Error,
}

/// <summary>
/// The logic of the quick panel (spec 7.1) without any window: the status icons and the state pill, "No telão agora", the field
/// with its "Vai aparecer assim" preview (700 ms after the last key), and the buttons Enviar, Urgente, Pausar/Retomar and Abrir
/// janela. The window only binds to it and forwards the keys.
/// </summary>
/// <remarks>
/// Changes that come from other threads (the status hub, the queue, the debounce timer) reach the properties through the
/// <c>post</c> function, which the app points at the UI dispatcher. The pause button calls <see cref="IAlertGateway.TogglePause"/>
/// and nothing else: the tray menu toggles through the shell, so this view-model never listens to a pause request.
/// </remarks>
public sealed partial class QuickPanelViewModel : ObservableObject, IDisposable
{
    /// <summary>How long the typing must rest before the preview is asked for.</summary>
    public static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(700);

    /// <summary>How long the message under the field stays.</summary>
    public static readonly TimeSpan FeedbackTime = TimeSpan.FromSeconds(4);

    private readonly IAlertGateway _gateway;
    private readonly StatusHub _hub;
    private readonly Func<double?> _progress;
    private readonly Action<Action> _post;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private ITimer? _previewTimer;
    private ITimer? _feedbackTimer;
    private CancellationTokenSource? _previewCts;
    private int _generation;
    private int _sending;
    private bool _disposed;
    private HotkeyStatus _hotkey = HotkeyStatus.None;

    [ObservableProperty]
    private StatusChip _telegram = new("Telegram", "LucideSend", StatusLight.Gray, "Telegram");

    [ObservableProperty]
    private StatusChip _site = new("Site", "LucideGlobe", StatusLight.Gray, "Site");

    [ObservableProperty]
    private StatusChip _led = new("LED", "LucideMonitor", StatusLight.Gray, "LED");

    [ObservableProperty]
    private string _pillText = "Pronta";

    [ObservableProperty]
    private StatusLight _pillLight = StatusLight.Green;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private string _currentText = string.Empty;

    [ObservableProperty]
    private string _currentSource = string.Empty;

    [ObservableProperty]
    private bool _hasCurrent;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _queueSummary = "Fila vazia";

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private PreviewView _preview = PreviewView.Empty;

    [ObservableProperty]
    private bool _isPreviewPending;

    [ObservableProperty]
    private string _feedbackText = string.Empty;

    [ObservableProperty]
    private FeedbackTone _feedbackTone;

    [ObservableProperty]
    private string _infoTip = string.Empty;

    [ObservableProperty]
    private string? _hotkeyMessage;

    /// <param name="gateway">The alert service as the operator uses it.</param>
    /// <param name="hub">The status of the parts.</param>
    /// <param name="progress">How far the alert on the bar has come (0 to 1), or null; read by <see cref="RefreshProgress"/>.</param>
    /// <param name="post">Runs an action on the UI thread; runs it at once when null (tests).</param>
    /// <param name="clock">Drives the preview delay and the message time.</param>
    /// <param name="logger">Receives failures.</param>
    public QuickPanelViewModel(
        IAlertGateway gateway,
        StatusHub hub,
        Func<double?>? progress = null,
        Action<Action>? post = null,
        TimeProvider? clock = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(hub);

        _gateway = gateway;
        _hub = hub;
        _progress = progress ?? (() => null);
        _post = post ?? (action => action());
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;

        SendCommand = new AsyncRelayCommand(() => SendAsync(urgent: false));
        SendUrgentCommand = new AsyncRelayCommand(() => SendAsync(urgent: true));
        TogglePauseCommand = new RelayCommand(TogglePause);
        OpenWindowCommand = new RelayCommand(OpenWindow);
        ClearCommand = new RelayCommand(() => InputText = string.Empty);

        _hub.PropertyChanged += OnHubChanged;
        _gateway.Queue.Changed += OnQueueChanged;
        RefreshStatus();
        RefreshQueue();
        UpdateInfoTip();
    }

    /// <summary>The panel should close (Esc on an empty field, or the window button was used).</summary>
    public event Action? CloseRequested;

    /// <summary>The user wants the full window (Abrir janela).</summary>
    public event Action? OpenMainWindowRequested;

    /// <summary>The field should take the keyboard focus (the panel was just shown).</summary>
    public event Action? FocusInputRequested;

    /// <summary>A send was refused, so the view may shake the preview.</summary>
    public event Action? SendRejected;

    /// <summary>Enter.</summary>
    public IAsyncRelayCommand SendCommand { get; }

    /// <summary>Ctrl+Enter and the Urgente button.</summary>
    public IAsyncRelayCommand SendUrgentCommand { get; }

    /// <summary>The Pausar/Retomar button: one press, one toggle.</summary>
    public IRelayCommand TogglePauseCommand { get; }

    /// <summary>The Abrir janela button.</summary>
    public IRelayCommand OpenWindowCommand { get; }

    /// <summary>Clears the field.</summary>
    public IRelayCommand ClearCommand { get; }

    /// <summary>Whether there is something to show in the "No telão agora" message area.</summary>
    public bool HasPreview => !Preview.IsEmpty;

    /// <summary>Whether the preview carries a warning line (a repeated plate).</summary>
    public bool HasPreviewNote => Preview.Note.Length > 0;

    /// <summary>Whether the message under the field is visible.</summary>
    public bool HasFeedback => FeedbackText.Length > 0;

    /// <summary>Whether the hotkey problem is visible.</summary>
    public bool HasHotkeyMessage => HotkeyMessage is not null;

    /// <summary>The resource key of the pause button's icon: pause, or play while paused.</summary>
    public string PauseIconKey => IsPaused ? "LucidePlay" : "LucidePause";

    /// <summary>The tooltip of the pause button.</summary>
    public string PauseTip => IsPaused ? "Retomar barra" : "Pausar barra";

    /// <summary>The preview of the last asked text finished (for tests and for the view to flash). Never null once a preview was asked.</summary>
    internal Task? LastPreviewTask { get; private set; }

    /// <summary>Whether a send is in progress.</summary>
    public bool IsSending => Volatile.Read(ref _sending) != 0;

    /// <summary>The panel was shown: take the focus, forget old messages, look at everything again.</summary>
    public void OnShown()
    {
        ClearFeedback();
        RefreshStatus();
        RefreshQueue();
        FocusInputRequested?.Invoke();
    }

    /// <summary>Esc: clears the field; on an empty field, closes the panel.</summary>
    public void Escape()
    {
        if (InputText.Length > 0)
        {
            InputText = string.Empty;
            return;
        }

        CloseRequested?.Invoke();
    }

    /// <summary>Tells the panel whether the global hotkey works; shows why it does not.</summary>
    public void ApplyHotkey(HotkeyStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        _hotkey = status;
        HotkeyMessage = status.Registered ? null : status.Message;
        UpdateInfoTip();
    }

    /// <summary>Moves the progress line to where the bar is. The window calls it a few times a second while it is visible.</summary>
    public void RefreshProgress()
    {
        ProgressValue = HasCurrent ? Math.Clamp(_progress() ?? 0, 0, 1) : 0;
    }

    /// <summary>
    /// Sends the field to the bar. A queued alert empties the field; anything else leaves the text so it can be fixed, and says
    /// why. Never throws.
    /// </summary>
    /// <param name="urgent">Whether it jumps ahead of the normal alerts.</param>
    public async Task SendAsync(bool urgent)
    {
        var raw = InputText.Trim();
        if (raw.Length == 0 || Interlocked.CompareExchange(ref _sending, 1, 0) != 0)
        {
            return;
        }

        CancelPreview();
        OnPropertyChanged(nameof(IsSending));
        try
        {
            var outcome = await _gateway.SubmitAsync(raw, urgent, CancellationToken.None);
            ShowOutcome(outcome, urgent);
        }
        catch (OperationCanceledException)
        {
            // Nothing was sent.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao enviar o aviso do painel rapido");
            SetFeedback("N\U000000E3o foi poss\U000000EDvel enviar. Veja o registro (log).", FeedbackTone.Error);
            SendRejected?.Invoke();
        }
        finally
        {
            Volatile.Write(ref _sending, 0);
            OnPropertyChanged(nameof(IsSending));
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
            _previewTimer?.Dispose();
            _feedbackTimer?.Dispose();
            _previewCts?.Cancel();
            _previewCts?.Dispose();
            _previewCts = null;
        }

        _hub.PropertyChanged -= OnHubChanged;
        _gateway.Queue.Changed -= OnQueueChanged;
    }

    // ---- property hooks ----

    partial void OnInputTextChanged(string value)
    {
        ClearFeedback();
        CancelPreview();
        if (string.IsNullOrWhiteSpace(value))
        {
            Preview = PreviewView.Empty;
            IsPreviewPending = false;
            return;
        }

        IsPreviewPending = true;
        StartPreviewTimer(value);
    }

    partial void OnPreviewChanged(PreviewView value)
    {
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(HasPreviewNote));
    }

    partial void OnFeedbackTextChanged(string value) => OnPropertyChanged(nameof(HasFeedback));

    partial void OnHotkeyMessageChanged(string? value) => OnPropertyChanged(nameof(HasHotkeyMessage));

    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(PauseIconKey));
        OnPropertyChanged(nameof(PauseTip));
    }

    // ---- buttons ----

    private void TogglePause()
    {
        _gateway.TogglePause();
    }

    private void OpenWindow()
    {
        OpenMainWindowRequested?.Invoke();
        CloseRequested?.Invoke();
    }

    private void ShowOutcome(SubmitOutcome outcome, bool urgent)
    {
        switch (outcome.Kind)
        {
            case SubmitKind.Queued:
                InputText = string.Empty;
                var place = urgent ? "Urgente na fila" : "Na fila";
                SetFeedback(
                    outcome.Position > 0 ? $"{place}: {outcome.Position}\U000000BA" : place,
                    FeedbackTone.Ok);
                break;

            case SubmitKind.PausedRefused:
                SetFeedback("Prega\U000000E7\U000000E3o em andamento: o aviso n\U000000E3o foi enviado.", FeedbackTone.Warn);
                SendRejected?.Invoke();
                break;

            case SubmitKind.Refused:
                Preview = PreviewPresenter.Error("conte\U000000FAdo impr\U000000F3prio");
                IsPreviewPending = false;
                SendRejected?.Invoke();
                break;

            case SubmitKind.TooLong:
                Preview = PreviewPresenter.Error($"texto grande demais (m\U000000E1ximo {AlertRules.MemberMaxLength} letras)");
                IsPreviewPending = false;
                SendRejected?.Invoke();
                break;

            case SubmitKind.Duplicate:
                SetFeedback(
                    outcome.DuplicateMinutesAgo is { } minutes
                        ? $"Esse carro j\U000000E1 passou h\U000000E1 {Math.Max(1, minutes)} min."
                        : "Esse carro j\U000000E1 est\U000000E1 na fila.",
                    FeedbackTone.Warn);
                SendRejected?.Invoke();
                break;

            default:
                Preview = PreviewPresenter.Error(
                    outcome.Missing.Count > 0 ? "falta " + string.Join(", ", outcome.Missing) : "nada para mostrar");
                IsPreviewPending = false;
                SendRejected?.Invoke();
                break;
        }
    }

    // ---- preview with a 700 ms rest ----

    private void StartPreviewTimer(string text)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var generation = ++_generation;
            _previewTimer?.Dispose();
            _previewTimer = _clock.CreateTimer(_ => BeginPreview(text, generation), null, PreviewDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void CancelPreview()
    {
        lock (_gate)
        {
            _generation++;
            _previewTimer?.Dispose();
            _previewTimer = null;
            _previewCts?.Cancel();
            _previewCts?.Dispose();
            _previewCts = null;
        }
    }

    private void BeginPreview(string text, int generation)
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed || generation != _generation)
            {
                return;
            }

            _previewCts?.Dispose();
            _previewCts = new CancellationTokenSource();
            token = _previewCts.Token;
        }

        LastPreviewTask = RunPreviewAsync(text.Trim(), generation, token);
    }

    private async Task RunPreviewAsync(string text, int generation, CancellationToken token)
    {
        PreviewView view;
        try
        {
            var result = await _gateway.PreviewAsync(text, token).ConfigureAwait(false);
            view = PreviewPresenter.Present(result);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha na previa do painel rapido");
            view = PreviewView.Empty;
        }

        _post(() =>
        {
            lock (_gate)
            {
                if (_disposed || generation != _generation)
                {
                    return;
                }
            }

            Preview = view;
            IsPreviewPending = false;
        });
    }

    // ---- messages under the field ----

    private void SetFeedback(string text, FeedbackTone tone)
    {
        FeedbackText = text;
        FeedbackTone = tone;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _feedbackTimer?.Dispose();
            _feedbackTimer = _clock.CreateTimer(_ => _post(ClearFeedback), null, FeedbackTime, Timeout.InfiniteTimeSpan);
        }
    }

    private void ClearFeedback()
    {
        lock (_gate)
        {
            _feedbackTimer?.Dispose();
            _feedbackTimer = null;
        }

        if (FeedbackText.Length > 0)
        {
            FeedbackText = string.Empty;
            FeedbackTone = FeedbackTone.None;
        }
    }

    // ---- status and queue ----

    private void OnHubChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => _post(RefreshStatus);

    private void OnQueueChanged(object? sender, EventArgs e) => _post(RefreshQueue);

    private void RefreshStatus()
    {
        if (_disposed)
        {
            return;
        }

        Telegram = Chip("Telegram", "LucideSend", _hub.Telegram);
        Site = Chip("Site", "LucideGlobe", _hub.Site);
        Led = Chip("LED", "LucideMonitor", _hub.Led);
        (PillText, PillLight) = _hub.Pill switch
        {
            AlertPill.Showing => ("Exibindo", StatusLight.Green),
            AlertPill.Paused => ("Pausada", StatusLight.Yellow),
            AlertPill.LedOff => ("LED desligado", StatusLight.Red),
            _ => ("Pronta", StatusLight.Green),
        };
        IsPaused = _hub.Paused;
    }

    private void RefreshQueue()
    {
        if (_disposed)
        {
            return;
        }

        var current = _gateway.Queue.Current;
        var pending = _gateway.Queue.Pending;
        HasCurrent = current is not null;
        CurrentText = current?.Text ?? string.Empty;
        CurrentSource = current is null ? string.Empty : SourceName(current.Source);
        QueueSummary = pending.Count == 0
            ? "Fila vazia"
            : $"Fila: {pending.Count} \U000000B7 pr\U000000F3xima: {pending[0].Text}";
        if (current is null)
        {
            ProgressValue = 0;
        }
    }

    private void UpdateInfoTip()
    {
        var hotkeyLine = _hotkey.Registered
            ? $"\n{_hotkey.Display}: abre este painel de qualquer programa"
            : "\nO atalho global n\U000000E3o est\U000000E1 dispon\U000000EDvel: use o \U000000EDcone da bandeja";
        InfoTip = "Enter: envia\nCtrl+Enter: urgente (fura a fila)\nEsc: limpa" + (_hotkey == HotkeyStatus.None ? string.Empty : hotkeyLine);
    }

    private static StatusChip Chip(string label, string icon, StatusIndicator indicator) =>
        new(label, icon, indicator.Light, indicator.Detail.Length > 0 ? $"{label}: {indicator.Text}\n{indicator.Detail}" : $"{label}: {indicator.Text}");

    private static string SourceName(AlertSource source) => source switch
    {
        AlertSource.Telegram => "TELEGRAM",
        AlertSource.Site => "SITE",
        _ => "PC",
    };
}

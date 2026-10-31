using AlertaIBT.Core.Holyrics;
using AlertaIBT.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AlertaIBT.App.ViewModels;

/// <summary>
/// The "Holyrics" card of the Controle tab: where the API Server of Holyrics is (host, port), the token that may use it, "Testar", the state chip, and the switch
/// "Setas do painel rápido controlam o Holyrics". Every change is saved at once through <see cref="SettingsService"/>; the token goes in protected (DPAPI) and is
/// never shown again, only dots. The same connection serves the rows 13 to 16 of the map and the arrows of the quick panel.
/// </summary>
public sealed partial class ControlViewModel
{
    /// <summary>How long the token entry must rest before it is kept (it is pasted, but a typed one must not be taken half way).</summary>
    public static readonly TimeSpan TokenDelay = TimeSpan.FromMilliseconds(700);

    private HolyricsClient? _holyricsClient;
    private ISecretProtector? _protector;
    private ITimer? _tokenTimer;
    private string? _pendingToken;
    private CancellationTokenSource? _testCts;

    [ObservableProperty]
    private string _holyricsHostText = HolyricsSettings.DefaultHost;

    [ObservableProperty]
    private bool _holyricsHostInvalid;

    [ObservableProperty]
    private bool _isTestingHolyrics;

    [ObservableProperty]
    private HolyricsLink _holyricsLink = HolyricsLink.Unknown;

    [ObservableProperty]
    private string _holyricsDetail = string.Empty;

    [ObservableProperty]
    private string _holyricsFailDetail = string.Empty;

    [ObservableProperty]
    private string _holyricsHostHint = string.Empty;

    /// <summary>The port box (1 to 65535).</summary>
    public IntegerField HolyricsPort { get; private set; } = null!;

    /// <summary>The API token: dots once stored, "Trocar" to type or paste a new one.</summary>
    public SecretFieldViewModel HolyricsToken { get; private set; } = null!;

    /// <summary>"Testar": asks Holyrics whether the token may do what the app needs, and reads the video player. Changes nothing in Holyrics.</summary>
    public IAsyncRelayCommand TestHolyricsCommand { get; private set; } = null!;

    /// <summary>Whether the Holyrics card works (a client was given; false only in tests of the rest of the tab).</summary>
    public bool HasHolyrics => _holyricsClient is not null;

    /// <summary>"Setas do painel rápido controlam o Holyrics" (on by default).</summary>
    public bool HolyricsArrows
    {
        get => _settings.Current.Holyrics.ArrowsEnabled;
        set
        {
            if (!_syncing && value != HolyricsArrows)
            {
                UpdateHolyrics(h => h with { ArrowsEnabled = value });
            }
        }
    }

    /// <summary>The state as a word the style reads (<c>ok</c>, <c>warn</c>, <c>bad</c>, <c>off</c>).</summary>
    public string HolyricsTone => HolyricsLink switch
    {
        HolyricsLink.Connected => "ok",
        HolyricsLink.Unauthorized or HolyricsLink.Waiting or HolyricsLink.Error => "warn",
        HolyricsLink.Unreachable => "bad",
        _ => "off",
    };

    /// <summary>The words of the chip: "conectado", or the reason of the failure (the chip trims it; the tooltip and the line under the card carry it whole).</summary>
    public string HolyricsStatusText => HolyricsLink switch
    {
        HolyricsLink.Connected => "conectado",
        HolyricsLink.Unauthorized => HolyricsDetail.Length > 0 ? HolyricsDetail : "token sem permiss\U000000E3o",
        HolyricsLink.Unreachable => HolyricsDetail.Length > 0 ? HolyricsDetail : "sem resposta",
        HolyricsLink.Waiting => HolyricsDetail.Length > 0 ? HolyricsDetail : "aguardando voc\U000000EA aprovar no Holyrics",
        HolyricsLink.Error => HolyricsDetail.Length > 0 ? HolyricsDetail : "erro do Holyrics",
        _ => HolyricsToken.HasStored ? "n\U000000E3o testado" : "sem token",
    };

    /// <summary>The tooltip of the chip: what the state means, and for a failure the full account (action, host:port, HTTP status, exception, time; never the token).</summary>
    public string HolyricsStatusTip
    {
        get
        {
            var tip = HolyricsLink switch
            {
                HolyricsLink.Connected => "O Holyrics respondeu e o token pode fazer o que o app precisa." + (HolyricsDetail.Length > 0 ? " " + HolyricsDetail : string.Empty),
                HolyricsLink.Unauthorized => "O Holyrics respondeu, mas n\U000000E3o aceitou o token ou a a\U000000E7\U000000E3o. No Holyrics: Arquivo > Configura\U000000E7\U000000F5es > API Server > gerenciar permiss\U000000F5es do token (ActionNext, ActionPrevious, MediaPlayerAction, GetMediaPlayerInfo, SetLogo). Um token novo pede aprova\U000000E7\U000000E3o na tela do Holyrics.",
                HolyricsLink.Unreachable => "O Holyrics n\U000000E3o respondeu: " + HolyricsStatusText + ". Confira se ele est\U000000E1 aberto, o servidor da API ligado, o endere\U000000E7o e a porta (padr\U000000E3o 8091).",
                HolyricsLink.Waiting => "O app est\U000000E1 esperando o Holyrics. Olhe a tela do Holyrics: pode haver um pedido para aprovar este token.",
                HolyricsLink.Error => "O Holyrics respondeu com um erro (veja abaixo).",
                _ => HolyricsToken.HasStored ? "Clique em Testar para conferir a conex\U000000E3o." : "Cole o token do Holyrics (Arquivo > Configura\U000000E7\U000000F5es > API Server).",
            };
            return HolyricsFailDetail.Length > 0 && HolyricsLink != HolyricsLink.Connected ? tip + "\n\n" + HolyricsFailDetail : tip;
        }
    }

    /// <summary>Whether the full reason shows under the card (a failure or a wait).</summary>
    public bool HasHolyricsReason => HolyricsLink is HolyricsLink.Unreachable or HolyricsLink.Unauthorized or HolyricsLink.Waiting or HolyricsLink.Error;

    /// <summary>The full reason, for the line under the card (the chip itself trims long words).</summary>
    public string HolyricsReasonText => HasHolyricsReason ? HolyricsStatusText : string.Empty;

    /// <summary>Whether a note about the address box shows (what was fixed, or why it is refused).</summary>
    public bool HasHolyricsHostHint => HolyricsHostHint.Length > 0;

    /// <summary>Whether the host box holds something that cannot be an address.</summary>
    public static bool IsValidHost(string? text) => HolyricsSettings.IsValidHost(text);

    /// <summary>"Colar": puts the clipboard text into the token entry (opens the field first when it is locked).</summary>
    /// <param name="text">The clipboard text; blank is ignored.</param>
    public void PasteHolyricsToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (HolyricsToken.IsLocked)
        {
            HolyricsToken.Unlock();
        }

        HolyricsToken.Entry = text.Trim();
    }

    /// <summary>Keeps the token that rested in the entry (the timer calls it; tests call it directly).</summary>
    public void ApplyPendingToken()
    {
        var token = _pendingToken;
        _pendingToken = null;
        if (token is null || _protector is null || _disposed)
        {
            return;
        }

        _settings.Update(s => s.WithHolyricsToken(token, _protector));
        HolyricsToken.Committed(); // dots again: the typed value is gone from memory here
        OnHolyricsChanged();
    }

    partial void OnHolyricsHostTextChanged(string value)
    {
        if (_syncing)
        {
            return;
        }

        var entry = HolyricsSettings.ParseHostEntry(value);
        HolyricsHostInvalid = !entry.Valid;
        HolyricsHostHint = entry.Hint ?? string.Empty;
        if (entry.Valid)
        {
            if (entry.Port is { } port)
            {
                HolyricsPort.Set(port); // "127.0.0.1:8091" typed in the address box: the port goes where it belongs
            }

            UpdateHolyrics(h => h with { Host = entry.Host, Port = entry.Port ?? h.Port });
        }
    }

    /// <summary>When the address box loses the focus: shows the clean address (no http://, port or slash) and keeps the note about what was fixed.</summary>
    public void NormalizeHolyricsHostText()
    {
        var entry = HolyricsSettings.ParseHostEntry(HolyricsHostText);
        if (!entry.Valid || entry.Host == HolyricsHostText)
        {
            return;
        }

        HolyricsHostText = entry.Host;
        HolyricsHostHint = entry.Hint ?? string.Empty;
    }

    partial void OnHolyricsLinkChanged(HolyricsLink value) => NotifyHolyricsStatus();

    partial void OnHolyricsDetailChanged(string value) => NotifyHolyricsStatus();

    partial void OnHolyricsFailDetailChanged(string value) => NotifyHolyricsStatus();

    partial void OnHolyricsHostHintChanged(string value) => OnPropertyChanged(nameof(HasHolyricsHostHint));

    partial void OnIsTestingHolyricsChanged(bool value) => TestHolyricsCommand?.NotifyCanExecuteChanged();

    private (IntegerField Port, SecretFieldViewModel Token, IAsyncRelayCommand Test) CreateHolyrics(HolyricsClient? client, ISecretProtector? protector)
    {
        _holyricsClient = client;
        _protector = protector;

        var port = new IntegerField(1, 65535, allowBlank: false, value => UpdateHolyrics(h => h with { Port = value ?? h.Port }), () => { });
        var token = new SecretFieldViewModel(
            () => _settings.Current.Holyrics.TokenProtected is not null,
            ValidateHolyricsToken,
            QueueToken,
            () =>
            {
                _pendingToken = null;
                _tokenTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            });
        token.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SecretFieldViewModel.HasStored))
            {
                NotifyHolyricsStatus();
                TestHolyricsCommand?.NotifyCanExecuteChanged();
            }
        };
        var test = new AsyncRelayCommand(TestHolyricsAsync, () => !IsTestingHolyrics && _holyricsClient is not null && _settings.Current.Holyrics.TokenProtected is not null);
        if (client is not null)
        {
            client.StatusChanged += OnHolyricsChanged;
        }

        return (port, token, test);
    }

    private static string? ValidateHolyricsToken(string token) =>
        token.Any(char.IsWhiteSpace) ? "o token n\U000000E3o tem espa\U000000E7os" : token.Length < 4 ? "token curto demais" : null;

    private void QueueToken(string token)
    {
        _pendingToken = token;
        _tokenTimer ??= TimeProvider.System.CreateTimer(_ => _post(ApplyPendingToken), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _tokenTimer.Change(TokenDelay, Timeout.InfiniteTimeSpan);
    }

    private async Task TestHolyricsAsync()
    {
        var client = _holyricsClient;
        if (client is null)
        {
            return;
        }

        IsTestingHolyrics = true;
        _testCts?.Cancel();
        var cts = _testCts = new CancellationTokenSource(HolyricsClient.PermissionTimeout + TimeSpan.FromSeconds(8));
        try
        {
            var result = await client.TestAsync(cts.Token).ConfigureAwait(true);
            HolyricsLink = result.Link;
            HolyricsDetail = result.Link == HolyricsLink.Connected
                ? result.Playing switch
                {
                    true => "v\U000000EDdeo tocando",
                    false => "v\U000000EDdeo parado",
                    _ => string.Empty,
                }
                : result.Message;
            HolyricsFailDetail = result.Link == HolyricsLink.Connected ? string.Empty : result.Detail;
        }
        catch (OperationCanceledException)
        {
            HolyricsLink = HolyricsLink.Unreachable;
            HolyricsDetail = "sem resposta: o teste demorou demais";
            HolyricsFailDetail = string.Empty;
        }
        finally
        {
            IsTestingHolyrics = false;
        }
    }

    private void UpdateHolyrics(Func<HolyricsSettings, HolyricsSettings> change) => _settings.Update(s => s with { Holyrics = change(s.Holyrics) });

    // The client says its state changed (a request ended, or the address/token changed): the chip follows it.
    private void OnHolyricsChanged() => _post(() =>
    {
        if (_disposed || _holyricsClient is null)
        {
            return;
        }

        var status = _holyricsClient.Status;
        if (status.Link == HolyricsLink.Unknown && HolyricsLink != HolyricsLink.Unknown)
        {
            HolyricsLink = HolyricsLink.Unknown;
            HolyricsDetail = string.Empty;
            HolyricsFailDetail = string.Empty;
        }
        else if (status.Link != HolyricsLink.Unknown)
        {
            HolyricsLink = status.Link;
            HolyricsDetail = status.Link == HolyricsLink.Connected ? HolyricsDetail : status.Message;
            HolyricsFailDetail = status.Link == HolyricsLink.Connected ? string.Empty : status.Detail;
        }

        NotifyHolyricsStatus();
    });

    private void NotifyHolyricsStatus()
    {
        OnPropertyChanged(nameof(HolyricsTone));
        OnPropertyChanged(nameof(HolyricsStatusText));
        OnPropertyChanged(nameof(HolyricsStatusTip));
        OnPropertyChanged(nameof(HasHolyricsReason));
        OnPropertyChanged(nameof(HolyricsReasonText));
    }

    // Reads the saved Holyrics settings into the screen without writing anything back (called by Load, with _syncing on).
    private void LoadHolyrics()
    {
        var holyrics = _settings.Current.Holyrics;
        HolyricsPort.Set(holyrics.Port);
        if (holyrics.Host != HolyricsHostText.Trim() && IsValidHost(HolyricsHostText))
        {
            HolyricsHostText = holyrics.Host;
        }

        HolyricsToken.Refresh();
        OnPropertyChanged(nameof(HolyricsArrows));
        OnPropertyChanged(nameof(HasHolyrics));
        NotifyHolyricsStatus();
        TestHolyricsCommand?.NotifyCanExecuteChanged();
    }
}

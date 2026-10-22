using System.Globalization;
using AlertaIBT.App.Platform;
using AlertaIBT.Core.Input;
using AlertaIBT.Core.Overlay;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.App.ViewModels;

/// <summary>
/// The logic of the Configurações tab (spec 7.2): the bar on the LED (position, size, speed and, in "Avançado", the separate font,
/// margin, colors and animation) with the live preview and "Mostrar barra 3 s no LED"; Telegram (token and chat id behind a lock);
/// the panel shortcut; and the Diagnóstico area. Every control saves 600 ms after its last change through the
/// <see cref="Autosaver"/>; a value that cannot be used is ignored with a hint and never saved.
/// </summary>
/// <remarks>
/// Each control saves only its own value, as a change to the settings of that moment, so a value written meanwhile by something
/// else (the Telegram offset, the remembered LED) is never overwritten with a stale copy. The preview follows the controls at once;
/// the real bar follows the saved settings.
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>The width of the LED, for the "passa em N s" estimate (the bar only ever runs on the 2560x768 screen).</summary>
    public const double LedWidth = 2560;

    private static readonly string[] SizeNames = ["Pequena", "M\U000000E9dia", "Grande", "Enorme"];

    private readonly SettingsService _settings;
    private readonly ISecretProtector _protector;
    private readonly StatusHub _hub;
    private readonly Autosaver _saver;
    private readonly Func<Task> _showBar;
    private readonly ILogger _logger;
    private readonly Action<Action> _post;
    private BarSettings _bar;
    private double _sampleWidth;
    private bool _quiet;
    private bool _disposed;
    private bool _isTop;

    [ObservableProperty]
    private BarSettings _previewBar;

    [ObservableProperty]
    private double _barHeight;

    [ObservableProperty]
    private double _barSpeed;

    [ObservableProperty]
    private string _sizeLabel = string.Empty;

    [ObservableProperty]
    private string _speedLabel = string.Empty;

    [ObservableProperty]
    private string _chatIdText = string.Empty;

    [ObservableProperty]
    private string? _chatIdHint;

    [ObservableProperty]
    private string _hotkeyDisplay = string.Empty;

    [ObservableProperty]
    private string? _hotkeyHint;

    [ObservableProperty]
    private string? _hotkeyProblem;

    [ObservableProperty]
    private string _barTestMessage = string.Empty;

    [ObservableProperty]
    private bool _isBarTestRunning;

    [ObservableProperty]
    private bool _siteEnabled;

    [ObservableProperty]
    private StatusLight _siteLight = StatusLight.Gray;

    [ObservableProperty]
    private string _siteText = string.Empty;

    /// <param name="settings">The settings.</param>
    /// <param name="protector">Protects the Telegram token (DPAPI in the app).</param>
    /// <param name="hub">Where the site line and the LED state come from.</param>
    /// <param name="saver">The autosave shared with the Correção tab.</param>
    /// <param name="diagnostics">The Diagnóstico area.</param>
    /// <param name="showBar">Plays the sample alert on the real bar for 3 seconds (<see cref="Overlay.OverlayController.PreviewAsync"/>).</param>
    /// <param name="post">Runs an action on the UI thread; runs it at once when null (tests).</param>
    /// <param name="logger">Receives failures (never a secret).</param>
    public SettingsViewModel(
        SettingsService settings,
        ISecretProtector protector,
        StatusHub hub,
        Autosaver saver,
        DiagnosticsViewModel diagnostics,
        Func<Task> showBar,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(saver);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(showBar);

        _settings = settings;
        _protector = protector;
        _hub = hub;
        _saver = saver;
        Diagnostics = diagnostics;
        _showBar = showBar;
        _post = post ?? (action => action());
        _logger = logger ?? NullLogger.Instance;

        FontSize = new IntegerField(
            BarSettings.MinFontSize,
            BarSettings.MaxFontSize,
            allowBlank: true,
            value => ChangeBar("bar.fontSize", b => b with { FontSize = value }),
            () => _saver.Cancel("bar.fontSize"));
        Margin = new IntegerField(
            0,
            BarSettings.MaxPaddingX,
            allowBlank: false,
            value => ChangeBar("bar.paddingX", b => b with { PaddingX = value ?? 0 }),
            () => _saver.Cancel("bar.paddingX"));
        SlideMs = new IntegerField(
            BarSettings.MinSlideMs,
            BarSettings.MaxSlideMs,
            allowBlank: false,
            value => ChangeBar("bar.slideMs", b => b with { SlideMs = value ?? 320 }),
            () => _saver.Cancel("bar.slideMs"));
        Background = new ColorField(
            color => ChangeBar("bar.background", b => b with { Background = color }),
            () => _saver.Cancel("bar.background"));
        Foreground = new ColorField(
            color => ChangeBar("bar.foreground", b => b with { Foreground = color }),
            () => _saver.Cancel("bar.foreground"));

        Token = new SecretFieldViewModel(
            () => _settings.Current.Telegram.TokenProtected is not null,
            ValidateToken,
            token => _saver.Queue(
                "telegram.token",
                s => s.WithTelegramToken(token, _protector), // a new token also sends the saved offset back to 0, in this same change
                () => Token!.Committed()),
            () => _saver.Cancel("telegram.token"));
        Token.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SecretFieldViewModel.IsLocked))
            {
                OnPropertyChanged(nameof(ChatIdEnabled));
            }
        };

        SiteUrl = new TextField(
            ValidateSiteUrl,
            url => _saver.Queue("site.url", s => s with { Site = s.Site with { DatabaseUrl = url } }),
            () => _saver.Cancel("site.url"));
        SiteApiKey = new TextField(
            ValidateApiKey,
            key => _saver.Queue("site.apiKey", s => s with { Site = s.Site with { ApiKey = key } }),
            () => _saver.Cancel("site.apiKey"));
        SiteEmail = new TextField(
            ValidateEmail,
            email => _saver.Queue("site.email", s => s with { Site = s.Site with { DeviceEmail = email } }),
            () => _saver.Cancel("site.email"));
        SitePassword = new SecretFieldViewModel(
            () => _settings.Current.Site.PasswordProtected is not null,
            ValidateSitePassword,
            password => _saver.Queue("site.password", s => s.WithSitePassword(password, _protector), () => SitePassword!.Committed()),
            () => _saver.Cancel("site.password"));

        ShowBarCommand = new AsyncRelayCommand(ShowBarAsync);

        _bar = _settings.Current.Bar;
        _previewBar = _bar;
        LoadFrom(_settings.Current);
        _hub.PropertyChanged += OnHubChanged;
        RefreshSite();
    }

    /// <summary>The separate font size; blank is automatic (64% of the height).</summary>
    public IntegerField FontSize { get; }

    /// <summary>The side margin of the text, in pixels.</summary>
    public IntegerField Margin { get; }

    /// <summary>How long the bar takes to slide in and out, in milliseconds.</summary>
    public IntegerField SlideMs { get; }

    /// <summary>The bar color.</summary>
    public ColorField Background { get; }

    /// <summary>The text color.</summary>
    public ColorField Foreground { get; }

    /// <summary>The Telegram token: locked with dots once stored.</summary>
    public SecretFieldViewModel Token { get; }

    /// <summary>The address of the Realtime Database of the site.</summary>
    public TextField SiteUrl { get; }

    /// <summary>The web API key of the Firebase project.</summary>
    public TextField SiteApiKey { get; }

    /// <summary>The e-mail of the PC own account in the Firebase project.</summary>
    public TextField SiteEmail { get; }

    /// <summary>The password of that account: locked with dots once stored.</summary>
    public SecretFieldViewModel SitePassword { get; }

    /// <summary>The Diagnóstico area (logs).</summary>
    public DiagnosticsViewModel Diagnostics { get; }

    /// <summary>"Mostrar barra 3 s no LED".</summary>
    public IAsyncRelayCommand ShowBarCommand { get; }

    /// <summary>The autosave stamp of this tab.</summary>
    public Autosaver Saver => _saver;

    /// <summary>The text the live preview and the real test show.</summary>
    public string SampleText => BarPreview.SampleText;

    /// <summary>The lowest bar height.</summary>
    public double MinHeight => BarSettings.MinHeight;

    /// <summary>The highest bar height.</summary>
    public double MaxHeight => BarSettings.MaxHeight;

    /// <summary>The slowest scroll.</summary>
    public double MinSpeed => BarSettings.MinSpeed;

    /// <summary>The fastest scroll.</summary>
    public double MaxSpeed => BarSettings.MaxSpeed;

    /// <summary>Whether the bar is at the top of the LED (the default).</summary>
    public bool IsTop
    {
        get => _isTop;
        set
        {
            if (_isTop == value)
            {
                return;
            }

            _isTop = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBottom));
            if (!_quiet)
            {
                ChangeBar("bar.position", b => b with { Position = value ? "top" : "bottom" });
            }
        }
    }

    /// <summary>Whether the bar is at the bottom of the LED. Setting false does nothing: choosing the other side does it.</summary>
    public bool IsBottom
    {
        get => !_isTop;
        set
        {
            if (value)
            {
                IsTop = false;
            }
        }
    }

    /// <summary>Whether the chat id box can be edited (it opens with the token's lock).</summary>
    public bool ChatIdEnabled => !Token.IsLocked;

    /// <summary>Whether the chat id hint is shown.</summary>
    public bool HasChatIdHint => ChatIdHint is not null;

    /// <summary>Whether the shortcut hint is shown.</summary>
    public bool HasHotkeyHint => HotkeyHint is not null;

    /// <summary>Whether the shortcut problem (another program owns it) is shown.</summary>
    public bool HasHotkeyProblem => HotkeyProblem is not null;

    /// <summary>Whether the message about the bar test is shown.</summary>
    public bool HasBarTestMessage => BarTestMessage.Length > 0;

    /// <summary>
    /// The live preview reports how wide the sample text is on the 2560 px LED, so the speed label can say how long a pass lasts.
    /// </summary>
    public void ReportSampleWidth(double widthPx)
    {
        if (!double.IsFinite(widthPx) || widthPx <= 0)
        {
            return;
        }

        _sampleWidth = widthPx;
        UpdateSpeedLabel();
    }

    /// <summary>
    /// Takes the shortcut the user pressed in the shortcut box. It needs Ctrl, Alt or Win and a key the app names, and it must not
    /// be a chord of editing or of the system (Ctrl+V, Alt+F4...); anything else is refused with a hint, the old shortcut stays and
    /// nothing is saved. The shell applies a saved shortcut by itself.
    /// </summary>
    /// <returns>Whether the shortcut was accepted.</returns>
    public bool TryCaptureHotkey(HotkeyModifiers modifiers, int virtualKey)
    {
        if (!HotkeyGesture.TryCreate(modifiers, virtualKey, out var gesture))
        {
            HotkeyHint = HotkeyPolicy.Hint(HotkeyGesture.Validate(modifiers, virtualKey));
            return false;
        }

        HotkeyHint = null;
        HotkeyDisplay = QuickPanelHotkey.Describe(gesture);
        var text = gesture.ToString();
        _saver.Queue("system.hotkey", s => s with { System = s.System with { Hotkey = text } });
        return true;
    }

    /// <summary>Tells the tab whether the global shortcut works; shows why it does not.</summary>
    public void ApplyHotkey(HotkeyStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        // The tab is where the shortcut is chosen, so the short reason plus the way out reads better than the long text of the panel.
        HotkeyProblem = status.Registered
            ? null
            : status.Short is { Length: > 0 } reason ? $"{reason}. Escolha outro no campo acima." : status.Message;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hub.PropertyChanged -= OnHubChanged;
        Diagnostics.Dispose();
    }

    partial void OnSiteEnabledChanged(bool value)
    {
        if (!_quiet)
        {
            _saver.Queue("site.enabled", s => s with { Site = s.Site with { Enabled = value } });
        }
    }

    partial void OnChatIdHintChanged(string? value) => OnPropertyChanged(nameof(HasChatIdHint));

    partial void OnHotkeyHintChanged(string? value) => OnPropertyChanged(nameof(HasHotkeyHint));

    partial void OnHotkeyProblemChanged(string? value) => OnPropertyChanged(nameof(HasHotkeyProblem));

    partial void OnBarTestMessageChanged(string value) => OnPropertyChanged(nameof(HasBarTestMessage));

    partial void OnBarHeightChanged(double value)
    {
        var height = Math.Clamp((int)Math.Round(value), BarSettings.MinHeight, BarSettings.MaxHeight);
        SizeLabel = SizeLabelFor(height);
        if (!_quiet)
        {
            ChangeBar("bar.height", b => b with { Height = height });
        }
    }

    partial void OnBarSpeedChanged(double value)
    {
        var speed = Math.Clamp((int)Math.Round(value), BarSettings.MinSpeed, BarSettings.MaxSpeed);
        UpdateSpeedLabel();
        if (!_quiet)
        {
            ChangeBar("bar.speed", b => b with { Speed = speed });
        }
    }

    partial void OnChatIdTextChanged(string value)
    {
        if (_quiet)
        {
            return;
        }

        var typed = value.Trim();
        if (typed.Length == 0)
        {
            ChatIdHint = null;
            QueueChat(0);
            return;
        }

        if (!long.TryParse(typed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var chat))
        {
            ChatIdHint = "Digite s\U000000F3 o n\U000000FAmero do grupo, como -1001234567890.";
            _saver.Cancel("telegram.chat");
            return;
        }

        ChatIdHint = null;
        QueueChat(chat);
    }

    private static string? ValidateToken(string token)
    {
        // The form Telegram's BotFather gives: the bot's number, a colon, then the secret part.
        var colon = token.IndexOf(':', StringComparison.Ordinal);
        var valid = colon > 0
            && token[..colon].All(char.IsAsciiDigit)
            && token.Length - colon - 1 >= 20
            && token[(colon + 1)..].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
        return valid ? null : "O token tem o formato 123456789:ABC... (cole inteiro, sem espa\U000000E7os).";
    }

    private static string? ValidateSiteUrl(string url) =>
        SiteSettings.IsValidDatabaseUrl(url)
            ? null
            : "Use o endereço do banco, como https://alertaibt-default-rtdb.firebaseio.com (https, terminando em firebaseio.com ou firebasedatabase.app).";

    private static string? ValidateApiKey(string key) =>
        key.Length >= 10 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? null
            : "A chave da API tem letras e números, sem espaços (começa com AIza).";

    private static string? ValidateEmail(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        var valid = at > 0 && at == email.LastIndexOf('@') && at < email.Length - 3 && email.IndexOf('.', at) > at + 1
            && !email.Any(char.IsWhiteSpace);
        return valid ? null : "Digite o e-mail da conta do PC, como pc-telao@alertaibt.web.app.";
    }

    private static string? ValidateSitePassword(string password) =>
        password.Length >= 6 ? null : "A senha tem pelo menos 6 caracteres.";

    private static string SizeLabelFor(int height)
    {
        var name = height <= 44 ? SizeNames[0] : height <= 64 ? SizeNames[1] : height <= 88 ? SizeNames[2] : SizeNames[3];
        return $"{name} \U000000B7 {height} px";
    }

    private void LoadFrom(AppSettings settings)
    {
        _quiet = true;
        try
        {
            var bar = settings.Bar;
            _isTop = !string.Equals(bar.Position, "bottom", StringComparison.Ordinal);
            OnPropertyChanged(nameof(IsTop));
            OnPropertyChanged(nameof(IsBottom));
            BarHeight = bar.Height;
            BarSpeed = bar.Speed;
            FontSize.Set(bar.FontSize);
            Margin.Set(bar.PaddingX);
            SlideMs.Set(bar.SlideMs);
            Background.Set(bar.Background);
            Foreground.Set(bar.Foreground);
            SiteEnabled = settings.Site.Enabled;
            SiteUrl.Set(settings.Site.DatabaseUrl);
            SiteApiKey.Set(settings.Site.ApiKey);
            SiteEmail.Set(settings.Site.DeviceEmail);
            ChatIdText = settings.Telegram.ChatId == 0 ? string.Empty : settings.Telegram.ChatId.ToString(CultureInfo.InvariantCulture);
            var hotkeyText = settings.System.Hotkey;

            // A value that cannot be used is replaced by the default when the shell applies it (and saved back): show that one.
            HotkeyDisplay = QuickPanelHotkey.Describe(QuickPanelHotkey.Parse(hotkeyText));
        }
        finally
        {
            _quiet = false;
        }

        SizeLabel = SizeLabelFor((int)BarHeight);
        UpdateSpeedLabel();
    }

    // The preview follows at once; the real settings follow 600 ms after the last change.
    private void ChangeBar(string key, Func<BarSettings, BarSettings> change)
    {
        _bar = change(_bar);
        PreviewBar = _bar;
        _saver.Queue(key, s => s with { Bar = change(s.Bar) });
    }

    private void QueueChat(long chat) =>
        _saver.Queue("telegram.chat", s => s with { Telegram = s.Telegram with { ChatId = chat } });

    private void UpdateSpeedLabel()
    {
        var speed = Math.Clamp((int)Math.Round(BarSpeed), BarSettings.MinSpeed, BarSettings.MaxSpeed);
        SpeedLabel = _sampleWidth > 0
            ? $"{speed} px/s \U000000B7 passa em {Math.Round((LedWidth + _sampleWidth) / speed).ToString("0", CultureInfo.InvariantCulture)} s"
            : $"{speed} px/s";
    }

    private async Task ShowBarAsync()
    {
        if (!_hub.LedPresent)
        {
            BarTestMessage = "O LED est\U000000E1 desligado: n\U000000E3o h\U000000E1 onde mostrar a barra.";
            return;
        }

        if (_hub.Showing)
        {
            BarTestMessage = "Um aviso est\U000000E1 passando agora. Tente de novo quando ele acabar.";
            return;
        }

        // The real bar reads the saved settings: what the sliders show now must be saved before it plays.
        _saver.FlushNow();
        IsBarTestRunning = true;
        BarTestMessage = "Mostrando a barra por 3 s no LED\U00002026";
        try
        {
            await _showBar();
            BarTestMessage = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao mostrar a barra de teste");
            BarTestMessage = "N\U000000E3o foi poss\U000000EDvel mostrar a barra. Veja o registro (log).";
        }
        finally
        {
            IsBarTestRunning = false;
        }
    }

    private void OnHubChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StatusHub.Site) or nameof(StatusHub.Led) or nameof(StatusHub.LedPresent))
        {
            _post(RefreshSite);
        }
    }

    private void RefreshSite()
    {
        if (_disposed)
        {
            return;
        }

        var site = _hub.Site;
        SiteLight = site.Light;
        SiteText = site.Text;
    }
}

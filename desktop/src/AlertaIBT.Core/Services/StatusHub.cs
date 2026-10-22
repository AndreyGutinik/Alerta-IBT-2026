using System.ComponentModel;
using AlertaIBT.Core.Firebase;
using AlertaIBT.Core.Telegram;

namespace AlertaIBT.Core.Services;

/// <summary>The color of a status dot.</summary>
public enum StatusLight
{
    /// <summary>Not in use or not known yet.</summary>
    Gray,

    /// <summary>Working.</summary>
    Green,

    /// <summary>Reconnecting, or paused.</summary>
    Yellow,

    /// <summary>Out of order.</summary>
    Red,

    /// <summary>Gemini is off and the offline corrector is working instead.</summary>
    Blue,
}

/// <summary>One status dot: its color, a short text and the longer detail for the tooltip.</summary>
/// <param name="Light">The color of the dot.</param>
/// <param name="Text">A word or two (<c>Conectado</c>).</param>
/// <param name="Detail">The longer explanation for the tooltip.</param>
public sealed record StatusIndicator(StatusLight Light, string Text, string Detail);

/// <summary>What the pill of the quick panel says.</summary>
public enum AlertPill
{
    /// <summary>Ready to show alerts (Pronta).</summary>
    Ready,

    /// <summary>An alert is on the bar (Exibindo).</summary>
    Showing,

    /// <summary>The sermon: nothing is shown and new alerts are refused (Pausada).</summary>
    Paused,

    /// <summary>The LED screen is off (LED desligado).</summary>
    LedOff,
}

/// <summary>Whether the Gemini corrector answered the last time it was asked.</summary>
public enum GeminiState
{
    /// <summary>It has not been asked yet.</summary>
    Unknown,

    /// <summary>The last answer arrived.</summary>
    Online,

    /// <summary>The last call failed: the offline corrector is working.</summary>
    Offline,
}

/// <summary>What the settings say about the correction.</summary>
public enum CorrectionMode
{
    /// <summary>The correction is switched off.</summary>
    Disabled,

    /// <summary>Only the offline corrector is used.</summary>
    OfflineOnly,

    /// <summary>Gemini first, the offline corrector as the fallback.</summary>
    Gemini,
}

/// <summary>Whether the local control API is listening.</summary>
public enum HttpApiState
{
    /// <summary>Not started.</summary>
    Stopped,

    /// <summary>Listening on 127.0.0.1.</summary>
    Listening,

    /// <summary>Another program holds the port.</summary>
    PortBusy,
}

/// <summary>
/// The status of the app's parts, for the status dots and the pill: Telegram, site, LED, bar, Gemini and the local API. The
/// parts report into it and the windows read from it. Each dot is a <see cref="StatusIndicator"/> that changes as a whole.
/// </summary>
/// <remarks>
/// Thread-safe. <see cref="PropertyChanged"/> is raised on the thread that reported the change, after the internal lock is
/// released; a WPF binding marshals it to the UI thread by itself, other listeners must do it. A listener that throws is
/// skipped.
/// </remarks>
public sealed class StatusHub : INotifyPropertyChanged
{
    private readonly object _gate = new();
    private Inputs _in = new();
    private string? _lastError;
    private DateTimeOffset? _lastErrorAt;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The Telegram dot.</summary>
    public StatusIndicator Telegram => Snapshot(TelegramDot);

    /// <summary>The site dot (gray "not configured" until the site is set up; see <see cref="SetSite"/>).</summary>
    public StatusIndicator Site => Snapshot(SiteDot);

    /// <summary>The LED dot.</summary>
    public StatusIndicator Led => Snapshot(LedDot);

    /// <summary>The bar dot (red when Windows refused to hide the bar from screen capture).</summary>
    public StatusIndicator Bar => Snapshot(BarDot);

    /// <summary>The Gemini dot (blue <c>Offline</c> when the offline corrector is in use).</summary>
    public StatusIndicator Gemini => Snapshot(GeminiDot);

    /// <summary>The local control API dot.</summary>
    public StatusIndicator Http => Snapshot(HttpDot);

    /// <summary>Where the Telegram connection stands.</summary>
    public TelegramState TelegramState => Snapshot(i => i.Telegram);

    /// <summary>Where the connection to the site stands.</summary>
    public SiteState SiteState => Snapshot(i => i.Site);

    /// <summary>Whether Gemini answered the last time it was asked.</summary>
    public GeminiState GeminiState => Snapshot(i => i.Gemini);

    /// <summary>Whether the overlay is paused (the sermon).</summary>
    public bool Paused => Snapshot(i => i.Paused);

    /// <summary>Whether an alert is on the bar.</summary>
    public bool Showing => Snapshot(i => i.Showing);

    /// <summary>Whether the LED screen is there.</summary>
    public bool LedPresent => Snapshot(i => i.LedPresent);

    /// <summary>The pill of the quick panel.</summary>
    public AlertPill Pill => Snapshot(PillOf);

    /// <summary>The last error reported by any part (with its source), or <c>null</c>.</summary>
    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    /// <summary>When <see cref="LastError"/> happened.</summary>
    public DateTimeOffset? LastErrorAt
    {
        get
        {
            lock (_gate)
            {
                return _lastErrorAt;
            }
        }
    }

    /// <summary>Sets the Telegram state and, when it is a problem, what went wrong.</summary>
    public void SetTelegram(TelegramState state, string? detail = null) =>
        Change(i => i with { Telegram = state, TelegramDetail = detail });

    /// <summary>Sets the site state and, when it is a problem, what went wrong (short, pt-BR, never a secret).</summary>
    public void SetSite(SiteState state, string? detail = null) =>
        Change(i => i with { Site = state, SiteDetail = detail });

    /// <summary>Sets whether the LED screen is there.</summary>
    public void SetLedPresent(bool present) => Change(i => i with { LedPresent = present });

    /// <summary>Sets the text that describes the LED (<c>2560x768 em \\.\DISPLAY2</c>), or <c>null</c> for none.</summary>
    public void SetLedDescription(string? description) => Change(i => i with { LedDescription = description });

    /// <summary>Sets whether the bar window is hidden from screen capture; <c>null</c> while the window does not exist yet.</summary>
    public void SetCaptureExcluded(bool? excluded) => Change(i => i with { CaptureExcluded = excluded });

    /// <summary>Sets whether Gemini answered the last time it was asked.</summary>
    public void SetGemini(bool online) => Change(i => i with { Gemini = online ? GeminiState.Online : GeminiState.Offline });

    /// <summary>Sets what the settings say about the correction.</summary>
    public void SetCorrectionMode(CorrectionMode mode) => Change(i => i with { Correction = mode });

    /// <summary>Sets whether the overlay is paused.</summary>
    public void SetPaused(bool paused) => Change(i => i with { Paused = paused });

    /// <summary>Sets whether an alert is on the bar.</summary>
    public void SetShowing(bool showing) => Change(i => i with { Showing = showing });

    /// <summary>Sets whether the local control API is listening; <paramref name="detail"/> says where, or why not.</summary>
    public void SetHttp(HttpApiState state, string? detail = null) =>
        Change(i => i with { Http = state, HttpDetail = detail });

    /// <summary>Records an error of a part of the app, so the windows can show it. Does not throw.</summary>
    /// <param name="source">Which part (<c>Telegram</c>, <c>API local</c>).</param>
    /// <param name="error">What went wrong.</param>
    /// <param name="now">When; the current time by default.</param>
    public void ReportError(string source, Exception error, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        lock (_gate)
        {
            _lastError = $"{source}: {error.Message}";
            _lastErrorAt = now ?? DateTimeOffset.UtcNow;
        }

        Raise([nameof(LastError), nameof(LastErrorAt)]);
    }

    private static StatusIndicator TelegramDot(Inputs i) => i.Telegram switch
    {
        TelegramState.NotConfigured => new(StatusLight.Gray, "N\U000000E3o configurado", "Falta o token do bot ou o ID do chat."),
        TelegramState.Connecting => new(StatusLight.Yellow, "Conectando", "Conectando ao Telegram."),
        TelegramState.Online => new(StatusLight.Green, "Conectado", "Recebendo as mensagens do grupo."),
        TelegramState.Reconnecting => new(StatusLight.Yellow, "Reconectando", i.TelegramDetail ?? "Sem resposta do Telegram; tentando de novo."),
        TelegramState.Offline => new(StatusLight.Red, "Sem conex\U000000E3o", i.TelegramDetail ?? "V\U000000E1rias falhas seguidas; tentando de novo."),
        TelegramState.Conflict => new(
            StatusLight.Red,
            "Conflito",
            "Outro programa est\U000000E1 usando o mesmo bot (o Alerta-IBT antigo est\U000000E1 aberto?). Feche-o para as mensagens chegarem aqui."),
        _ => new(StatusLight.Gray, "?", string.Empty),
    };

    private static StatusIndicator SiteDot(Inputs i) => i.Site switch
    {
        SiteState.Connecting => new(StatusLight.Yellow, "Conectando", "Conectando ao site."),
        SiteState.Online => new(StatusLight.Green, "Conectado", "Recebendo os avisos enviados pelo site."),
        SiteState.Reconnecting => new(StatusLight.Yellow, "Reconectando", i.SiteDetail ?? "Sem resposta do site; tentando de novo."),
        SiteState.Offline => new(StatusLight.Red, "Sem conex\U000000E3o", i.SiteDetail ?? "O site n\U000000E3o responde h\U000000E1 mais de um minuto; tentando de novo."),
        SiteState.AuthError => new(StatusLight.Red, "Erro de login", i.SiteDetail ?? "O Firebase recusou o login do PC. Confira o e-mail, a senha e a chave da API em Configura\U000000E7\U000000F5es."),
        _ => new(StatusLight.Gray, "N\U000000E3o configurado", "O site ainda n\U000000E3o est\U000000E1 ligado a este PC."),
    };

    private static StatusIndicator LedDot(Inputs i) => i.LedPresent
        ? new(StatusLight.Green, "Ligado", i.LedDescription ?? "Tel\U000000E3o encontrado.")
        : new(StatusLight.Red, "Desligado", "Nenhum monitor de 2560x768 encontrado. Os avisos esperam na fila at\U000000E9 ele ligar.");

    private static StatusIndicator BarDot(Inputs i)
    {
        if (i.CaptureExcluded == false)
        {
            return new(
                StatusLight.Red,
                "Aparece na captura",
                "O Windows n\U000000E3o escondeu a barra da captura de tela: o OBS ou o NDI podem mostr\U000000E1-la. Veja o log.");
        }

        if (i.Paused)
        {
            return new(StatusLight.Yellow, "Pausada", "Prega\U000000E7\U000000E3o em andamento: a barra n\U000000E3o mostra avisos.");
        }

        if (!i.LedPresent)
        {
            return new(StatusLight.Gray, "Escondida", "Sem o tel\U000000E3o, a barra fica escondida.");
        }

        return i.Showing
            ? new(StatusLight.Green, "Exibindo", "Um aviso est\U000000E1 passando no tel\U000000E3o.")
            : new(StatusLight.Green, "Pronta", "Pronta para mostrar avisos.");
    }

    private static StatusIndicator GeminiDot(Inputs i) => i.Correction switch
    {
        CorrectionMode.Disabled => new(StatusLight.Gray, "Desligada", "A corre\U000000E7\U000000E3o autom\U000000E1tica est\U000000E1 desligada."),
        CorrectionMode.OfflineOnly => new(StatusLight.Blue, "Offline", "Corre\U000000E7\U000000E3o s\U000000F3 offline (escolha nas configura\U000000E7\U000000F5es)."),
        _ => i.Gemini switch
        {
            GeminiState.Online => new(StatusLight.Green, "Online", "O Gemini est\U000000E1 corrigindo os avisos."),
            GeminiState.Offline => new(
                StatusLight.Blue,
                "Offline",
                "O Gemini n\U000000E3o respondeu (sem chave, sem internet ou cota). O corretor offline est\U000000E1 em uso e o Gemini volta sozinho."),
            _ => new(StatusLight.Gray, "Aguardando", "O Gemini ainda n\U000000E3o foi usado."),
        },
    };

    private static StatusIndicator HttpDot(Inputs i) => i.Http switch
    {
        HttpApiState.Listening => new(StatusLight.Green, "Ouvindo", i.HttpDetail ?? "API local ativa."),
        HttpApiState.PortBusy => new(StatusLight.Red, "Porta ocupada", i.HttpDetail ?? "Outro programa usa a porta da API local."),
        _ => new(StatusLight.Gray, "Parada", "A API local n\U000000E3o est\U000000E1 ativa."),
    };

    private static AlertPill PillOf(Inputs i)
    {
        if (i.Paused)
        {
            return AlertPill.Paused;
        }

        if (!i.LedPresent)
        {
            return AlertPill.LedOff;
        }

        return i.Showing ? AlertPill.Showing : AlertPill.Ready;
    }

    private T Snapshot<T>(Func<Inputs, T> read)
    {
        lock (_gate)
        {
            return read(_in);
        }
    }

    private void Change(Func<Inputs, Inputs> change)
    {
        List<string> names = [];
        lock (_gate)
        {
            var before = _in;
            var after = change(before);
            if (after == before)
            {
                return;
            }

            _in = after;
            AddIfChanged(names, nameof(Telegram), TelegramDot(before), TelegramDot(after));
            AddIfChanged(names, nameof(Site), SiteDot(before), SiteDot(after));
            AddIfChanged(names, nameof(SiteState), before.Site, after.Site);
            AddIfChanged(names, nameof(Led), LedDot(before), LedDot(after));
            AddIfChanged(names, nameof(Bar), BarDot(before), BarDot(after));
            AddIfChanged(names, nameof(Gemini), GeminiDot(before), GeminiDot(after));
            AddIfChanged(names, nameof(Http), HttpDot(before), HttpDot(after));
            AddIfChanged(names, nameof(TelegramState), before.Telegram, after.Telegram);
            AddIfChanged(names, nameof(GeminiState), before.Gemini, after.Gemini);
            AddIfChanged(names, nameof(Paused), before.Paused, after.Paused);
            AddIfChanged(names, nameof(Showing), before.Showing, after.Showing);
            AddIfChanged(names, nameof(LedPresent), before.LedPresent, after.LedPresent);
            AddIfChanged(names, nameof(Pill), PillOf(before), PillOf(after));
        }

        Raise(names);
    }

    private static void AddIfChanged<T>(List<string> names, string name, T before, T after)
    {
        if (!EqualityComparer<T>.Default.Equals(before, after))
        {
            names.Add(name);
        }
    }

    private void Raise(IReadOnlyList<string> names)
    {
        var handlers = PropertyChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (var name in names)
        {
            foreach (var handler in handlers.GetInvocationList().Cast<PropertyChangedEventHandler>())
            {
                try
                {
                    handler(this, new PropertyChangedEventArgs(name));
                }
                catch (Exception)
                {
                    // A listener that throws must not stop the others, nor the part that reported the change.
                }
            }
        }
    }

    private sealed record Inputs
    {
        public TelegramState Telegram { get; init; } = TelegramState.NotConfigured;

        public string? TelegramDetail { get; init; }

        public SiteState Site { get; init; } = SiteState.NotConfigured;

        public string? SiteDetail { get; init; }

        public bool LedPresent { get; init; }

        public string? LedDescription { get; init; }

        public bool? CaptureExcluded { get; init; }

        public GeminiState Gemini { get; init; } = GeminiState.Unknown;

        public CorrectionMode Correction { get; init; } = CorrectionMode.Gemini;

        public bool Paused { get; init; }

        public bool Showing { get; init; }

        public HttpApiState Http { get; init; } = HttpApiState.Stopped;

        public string? HttpDetail { get; init; }
    }
}

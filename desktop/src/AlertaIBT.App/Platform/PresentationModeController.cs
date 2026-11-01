using System.Threading.Channels;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Holyrics;
using AlertaIBT.Core.Input;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.App.Platform;

/// <summary>
/// The presentation mode ("Modo apresentacao"): the pastor's clicker is only a keyboard to Windows, so while the mode is ON a keyboard hook takes the
/// chosen keys (Page Down / right arrow = next, Page Up / left arrow = previous, by default) wherever the focus is, swallows them, and sends
/// <c>ActionNext</c> / <c>ActionPrevious</c> to Holyrics through <see cref="IHolyricsRemote"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Comes back as it was at the last close</b> (<see cref="HolyricsSettings.PresentationMode"/>, see <see cref="RestoreSavedState"/>); OFF on a first install. Turning ON needs Holyrics configured (a token) and a keyboard hook that Windows accepts;
/// otherwise the mode stays OFF and <see cref="Reason"/> says why. It turns itself OFF, with the reason, when the token is removed or the hook cannot be kept.
/// </para>
/// <para>
/// <b>The hook callback only decides and enqueues</b> (<see cref="PresentationKeyFilter"/>, then a lock-free channel write): no HTTP, no UI, no log. A background task
/// reads the channel and calls <see cref="IHolyricsRemote.Post"/>, which itself only queues. So the callback stays at microseconds, far below the ~300 ms after which
/// Windows drops a hook. As a safety net the hook is replaced by a fresh one every <see cref="RefreshInterval"/>, which also renews a hook Windows dropped silently.
/// </para>
/// <para>
/// The hook is a second one, separate from the passive hook of Ctrl x3 and the media keys, and exists only while the mode is ON. Everything here runs on the UI
/// thread (the thread that owns the hook); <see cref="SetOn"/> may be called from any thread and hands over to it.
/// </para>
/// </remarks>
public sealed class PresentationModeController : IPresentationControl, IDisposable
{
    /// <summary>The shortcut that turns the mode on and off.</summary>
    public const string HotkeyText = "Ctrl+Alt+P";

    /// <summary>The text of the refusal when Holyrics has no token.</summary>
    public const string NotConfiguredText = "Holyrics sem token (aba Controle)";

    /// <summary>How often the hook is replaced by a fresh one while the mode is on.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly SettingsService _settings;
    private readonly IHolyricsRemote _remote;
    private readonly IKeyboardGate _gate;
    private readonly Func<Func<PresentationResult>, PresentationResult> _invoke;
    private readonly ILogger? _logger;
    private readonly Func<int, bool> _panelHandlesKey;
    private readonly Func<bool> _modifierHeld;
    private readonly TimeProvider _clock;
    private readonly PresentationKeyFilter _filter = new();
    private readonly KeyConsumer _consumer;
    private QuickPanelHotkey? _hotkey;
    private Channel<PresentationKeyAction>? _queue;
    private ITimer? _refresh;
    private volatile bool _on;
    private bool _started;
    private bool _disposed;
    private string? _reason;

    /// <param name="settings">The settings (the chosen keys, and the connection that says whether Holyrics is configured).</param>
    /// <param name="remote">Holyrics.</param>
    /// <param name="gate">The swallowing keyboard hook.</param>
    /// <param name="invoke">Runs a piece of work on the UI thread and returns its result (at once when already there). Tests run it in place.</param>
    /// <param name="logger">Receives what happens (never a key).</param>
    /// <param name="panelHandlesKey">Whether the quick panel's own arrow path handles this key now (the real check when null).</param>
    /// <param name="modifierHeld">Whether Ctrl, Alt, Shift or Win is held; the real check when null.</param>
    /// <param name="clock">Times the hook refresh.</param>
    public PresentationModeController(
        SettingsService settings,
        IHolyricsRemote remote,
        IKeyboardGate gate,
        Func<Func<PresentationResult>, PresentationResult> invoke,
        ILogger? logger = null,
        Func<int, bool>? panelHandlesKey = null,
        Func<bool>? modifierHeld = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(invoke);

        _settings = settings;
        _remote = remote;
        _gate = gate;
        _invoke = invoke;
        _logger = logger;
        _panelHandlesKey = panelHandlesKey ?? QuickPanelHandles;
        _modifierHeld = modifierHeld ?? AnyModifierHeld;
        _clock = clock ?? TimeProvider.System;
        _consumer = OnKey;
        ApplyKeys(settings.Current.Holyrics);
    }

    /// <summary>Whether the mode is on.</summary>
    public bool IsOn => _on;

    /// <summary>Why the mode is off when it was asked to turn on (or turned itself off): a pt-BR sentence; <c>null</c> when there is nothing to say.</summary>
    public string? Reason => _reason;

    /// <summary>Whether the mode can be turned on now (Holyrics has a token).</summary>
    public bool CanTurnOn => _remote.IsConfigured;

    /// <summary>Whether the keyboard hook is installed (only while the mode is on).</summary>
    public bool HookInstalled => _gate.IsInstalled;

    /// <summary>Whether the Ctrl+Alt+P shortcut works and, if not, why.</summary>
    public HotkeyStatus HotkeyStatus => _hotkey?.Status ?? HotkeyStatus.None;

    /// <summary>The mode or its reason changed (UI thread).</summary>
    public event Action? StateChanged;

    /// <summary>The shortcut was applied (start).</summary>
    public event Action<HotkeyStatus>? HotkeyStatusChanged;

    /// <summary>Registers Ctrl+Alt+P and follows the settings. Call on the UI thread, once. The mode stays OFF.</summary>
    /// <param name="registrar">Registers the shortcut with the system.</param>
    public void Start(IHotkeyRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);

        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _hotkey = new QuickPanelHotkey(
            registrar,
            _logger,
            HotkeyText,
            "do modo apresentacao",
            display => $"O atalho {display} (modo apresenta\U000000E7\U000000E3o) j\U000000E1 \U000000E9 usado por outro programa. Use o interruptor em Controle ou o menu da bandeja.");
        _hotkey.StatusChanged += OnHotkeyStatusChanged;
        _hotkey.Apply(HotkeyText);
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>The shortcut or the tray item: flips the mode. On the UI thread.</summary>
    public void Toggle()
    {
        if (!_disposed)
        {
            SetOnCore(!_on);
        }
    }

    /// <inheritdoc />
    public PresentationResult SetOn(bool on)
    {
        if (_disposed)
        {
            return PresentationResult.Fail("app encerrando");
        }

        try
        {
            return _invoke(() => SetOnCore(on));
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            return PresentationResult.Fail("o app n\U000000E3o respondeu");
        }
    }

    /// <summary>Turns the mode off and lets go of the hook. Never throws.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        if (_hotkey is not null)
        {
            _hotkey.StatusChanged -= OnHotkeyStatusChanged;
        }

        try
        {
            Stop();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Soltar o gancho do modo apresentacao falhou");
        }
    }

    // persist: true for what the owner does (switch, shortcut, tray, API): the new position becomes the default of the next start. The restore at start does not
    // write: a start that could not turn it on (no token yet, hook refused) keeps the owner's wish and tries once at the next start.
    private PresentationResult SetOnCore(bool on, bool persist = true)
    {
        if (on == _on)
        {
            if (persist)
            {
                SaveWish(on);
            }

            return PresentationResult.Ok;
        }

        if (!on)
        {
            Stop();
            SetReason(null);
            Breadcrumbs.Add("modo apresentacao: desligado");
            _logger?.LogInformation("Modo apresentacao desligado");
            if (persist)
            {
                SaveWish(false);
            }

            return PresentationResult.Ok;
        }

        if (!_remote.IsConfigured)
        {
            return Refuse(NotConfiguredText);
        }

        ApplyKeys(_settings.Current.Holyrics);
        _filter.Reset();
        _filter.Configured = true;
        _queue = Channel.CreateUnbounded<PresentationKeyAction>(new UnboundedChannelOptions { SingleReader = true });
        _ = Task.Run(() => PumpAsync(_queue.Reader));
        if (!_gate.Install(_consumer))
        {
            _queue.Writer.TryComplete();
            _queue = null;
            _filter.Configured = false;
            return Refuse("o Windows recusou o gancho do teclado");
        }

        _filter.On = true;
        _on = true;
        _refresh = _clock.CreateTimer(_ => _ = SafeInvoke(Refresh), null, RefreshInterval, RefreshInterval);
        SetReason(null);
        Breadcrumbs.Add("modo apresentacao: ligado");
        _logger?.LogInformation("Modo apresentacao ligado");
        if (persist)
        {
            SaveWish(true);
        }

        StateChanged?.Invoke();
        return PresentationResult.Ok;
    }

    /// <summary>
    /// Puts the mode back as the owner left it (call once at start, on the UI thread, after <see cref="Start"/>). ON is restored through the same path as the switch, so it needs
    /// the Holyrics token and a keyboard hook Windows accepts; if either is missing it stays OFF, <see cref="Reason"/> says why, and nothing is retried before the next start.
    /// </summary>
    public void RestoreSavedState()
    {
        if (_disposed || !_settings.Current.Holyrics.PresentationMode)
        {
            return;
        }

        _logger?.LogInformation("Modo apresentacao: restaurando o estado salvo (ligado)");
        SetOnCore(true, persist: false);
    }

    private void SaveWish(bool on) =>
        _settings.Update(s => s.Holyrics.PresentationMode == on ? s : s with { Holyrics = s.Holyrics with { PresentationMode = on } });

    private PresentationResult Refuse(string reason)
    {
        SetReason(reason);
        Breadcrumbs.Add("modo apresentacao: recusado");
        _logger?.LogWarning("Modo apresentacao recusado: {Reason}", reason);
        return PresentationResult.Fail(reason);
    }

    // Turns everything off: first the filter (nothing is swallowed from now on, except the up of a key already held), then the hook, the timer and the queue.
    private void Stop()
    {
        var was = _on;
        _filter.On = false;
        _filter.Configured = false;
        _on = false;
        _refresh?.Dispose();
        _refresh = null;
        _gate.Uninstall();
        _queue?.Writer.TryComplete();
        _queue = null;
        if (was)
        {
            StateChanged?.Invoke();
        }
    }

    // The hook may have been dropped by Windows without a word: a fresh one replaces it every 30 s. If a fresh one cannot be had, the mode goes off and says so.
    private PresentationResult Refresh()
    {
        if (_on && !_disposed && !_gate.Refresh())
        {
            Fail("o Windows removeu o gancho do teclado");
        }

        return PresentationResult.Ok;
    }

    private PresentationResult SafeInvoke(Func<PresentationResult> work)
    {
        try
        {
            return _invoke(work);
        }
        catch (Exception)
        {
            return PresentationResult.Ok; // the app is closing: nothing to refresh
        }
    }

    private void Fail(string reason)
    {
        Stop();
        SetReason(reason);
        Breadcrumbs.Add("modo apresentacao: desligado sozinho");
        _logger?.LogWarning("Modo apresentacao desligado sozinho: {Reason}", reason);
    }

    private void SetReason(string? reason)
    {
        if (_reason != reason)
        {
            _reason = reason;
            StateChanged?.Invoke();
        }
    }

    // Runs inside the hook, on the UI thread: decide, and queue the command. Nothing slow, nothing that touches the UI, never an exception into Windows.
    private bool OnKey(int virtualKey, bool keyDown)
    {
        try
        {
            if (!_filter.Matters(virtualKey, keyDown))
            {
                return false;
            }

            var verdict = _filter.Decide(new PresentationKeyEvent(virtualKey, keyDown, keyDown && _modifierHeld(), keyDown && _panelHandlesKey(virtualKey)));
            if (verdict.Action != PresentationKeyAction.None)
            {
                _queue?.Writer.TryWrite(verdict.Action);
            }

            return verdict.Consume;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Background: one command after another, in the order the keys were pressed. Post only queues for Holyrics, so this never waits for the network.
    private async Task PumpAsync(ChannelReader<PresentationKeyAction> reader)
    {
        try
        {
            await foreach (var action in reader.ReadAllAsync().ConfigureAwait(false))
            {
                _remote.Post(new HolyricsCommand(action == PresentationKeyAction.Next ? HolyricsActionKind.SlideNext : HolyricsActionKind.SlidePrevious));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "O envio do modo apresentacao falhou");
        }
    }

    private void ApplyKeys(HolyricsSettings holyrics) => _filter.SetKeys(holyrics.PresentationNext, holyrics.PresentationPrevious);

    private void OnSettingsChanged(AppSettings changed)
    {
        if (_disposed)
        {
            return;
        }

        // Any thread may have changed the settings; the hook and the mode belong to the UI thread.
        _ = SafeInvoke(() =>
        {
            if (_disposed)
            {
                return PresentationResult.Ok;
            }

            ApplyKeys(_settings.Current.Holyrics);
            if (_on && !_remote.IsConfigured)
            {
                Fail(NotConfiguredText);
                SaveWish(false); // the token was removed on purpose: the switch is visibly off, so off is also the default of the next start
            }
            else
            {
                StateChanged?.Invoke(); // the token may have just been filled in: the switch can turn on
            }

            return PresentationResult.Ok;
        });
    }

    private void OnHotkeyStatusChanged(HotkeyStatus status)
    {
        if (status.Registered)
        {
            _logger?.LogInformation("Atalho do modo apresentacao: {Hotkey}", status.Text);
        }
        else
        {
            _logger?.LogWarning("Atalho do modo apresentacao indisponivel: {Hotkey} ({Reason})", status.Text, status.Short);
        }

        HotkeyStatusChanged?.Invoke(status);
        StateChanged?.Invoke();
    }

    private static bool AnyModifierHeld() =>
        IsDown(0x10) || IsDown(0x11) || IsDown(0x12) || IsDown(0x5B) || IsDown(0x5C);

    private static bool IsDown(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    // Left/Right with the text box of the quick panel focused and the arrows switch on: the panel itself sends the slide (and handles the key), so the hook stays out of it.
    // Every other key, and every other box of this app, is taken like in any other window. Runs on the UI thread (the hook's thread): reading the focus is cheap.
    private bool QuickPanelHandles(int virtualKey)
    {
        if (virtualKey is not (0x25 or 0x27) || !_remote.ArrowsActive)
        {
            return false;
        }

        foreach (var window in System.Windows.Application.Current?.Windows.OfType<AlertaIBT.App.Tray.QuickPanel>() ?? [])
        {
            if (window.IsVisible && window.InputHasFocus())
            {
                return true;
            }
        }

        return false;
    }
}

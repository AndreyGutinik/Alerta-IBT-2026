using AlertaIBT.Core.Input;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.App.Platform;

/// <summary>Registers one global hotkey with the system. The real one is <see cref="HotkeyService"/>; tests use a fake.</summary>
public interface IHotkeyRegistrar
{
    /// <summary>Registers <paramref name="gesture"/>, replacing the previous one. False when the system refuses it.</summary>
    bool Register(HotkeyGesture gesture);

    /// <summary>Gives the hotkey back.</summary>
    void Unregister();
}

/// <summary>Whether the quick-panel hotkey works, and if not, why, in words for the user.</summary>
/// <param name="Text">The hotkey as the settings hold it (<c>Ctrl+Alt+Space</c>).</param>
/// <param name="Display">The hotkey as the user reads it (<c>Ctrl+Alt+Espaco</c>).</param>
/// <param name="Registered">Whether Windows accepted it.</param>
/// <param name="Message">What to tell the user when it is not registered; <c>null</c> when it is.</param>
/// <param name="Short">The same in a few words, for the tray tooltip; <c>null</c> when it is registered.</param>
/// <param name="FellBackFrom">
/// The value of the settings that could not be used (empty, malformed, or a reserved chord such as Ctrl+V), when
/// <paramref name="Text"/> is the default that took its place; <c>null</c> otherwise.
/// </param>
public sealed record HotkeyStatus(string Text, string Display, bool Registered, string? Message, string? Short = null, string? FellBackFrom = null)
{
    /// <summary>Nothing applied yet.</summary>
    public static HotkeyStatus None { get; } = new(string.Empty, string.Empty, false, null);
}

/// <summary>
/// Applies the hotkey of the settings (modifiers plus one key, as text such as <c>Ctrl+Alt+Space</c>) and remembers whether it
/// took. Another program may own the combination or the text may be invalid: that is never an error for the app, it is a
/// <see cref="HotkeyStatus"/> with a message that the panel and the tray tooltip show, while the tray click keeps working.
/// A saved value that is not a valid shortcut (including the chords of <see cref="HotkeyPolicy"/>) is never registered: the
/// default takes its place, with one line in the log, and <see cref="HotkeyStatus.FellBackFrom"/> tells the shell to save it back.
/// </summary>
public sealed class QuickPanelHotkey
{
    private readonly IHotkeyRegistrar _registrar;
    private readonly ILogger? _logger;
    private string? _applied;

    /// <summary>The shortcut used when the saved one cannot be (the default of the settings).</summary>
    public static string DefaultText { get; } = new SystemSettings().Hotkey;

    /// <param name="registrar">The system registration.</param>
    /// <param name="logger">Receives the line about a saved value that was replaced by the default.</param>
    public QuickPanelHotkey(IHotkeyRegistrar registrar, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registrar);

        _registrar = registrar;
        _logger = logger;
    }

    /// <summary>The gesture of <paramref name="text"/>, or the default one when the text cannot be used.</summary>
    public static HotkeyGesture Parse(string? text) =>
        HotkeyGesture.TryParse(text, out var gesture) ? gesture : HotkeyGesture.TryParse(DefaultText, out var fallback)
            ? fallback
            : new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x20);

    /// <summary>Raised when <see cref="Status"/> changed after an <see cref="Apply"/>.</summary>
    public event Action<HotkeyStatus>? StatusChanged;

    /// <summary>What the last <see cref="Apply"/> found.</summary>
    public HotkeyStatus Status { get; private set; } = HotkeyStatus.None;

    /// <summary>
    /// Registers the hotkey in <paramref name="hotkeyText"/>, unless that very text is already applied. Never throws: a system
    /// that fails in an unexpected way is reported like a combination in use.
    /// </summary>
    public void Apply(string? hotkeyText) => Apply(hotkeyText ?? string.Empty, force: false);

    /// <summary>
    /// Tries again to register the hotkey that is applied but not working (the program that owned the combination may have
    /// closed since). Does nothing when it works or none was applied; <see cref="StatusChanged"/> is raised only if it got better.
    /// </summary>
    public void Retry()
    {
        if (_applied is not null && !Status.Registered)
        {
            Apply(_applied, force: true);
        }
    }

    private void Apply(string text, bool force)
    {
        if (!force && string.Equals(text, _applied, StringComparison.Ordinal))
        {
            return;
        }

        string? fellBackFrom = null;
        if (!HotkeyGesture.TryParse(text, out var gesture))
        {
            // Not a shortcut to register: the default takes its place (and the shell saves it back).
            fellBackFrom = text;
            text = DefaultText;
            gesture = Parse(text);
            _logger?.LogWarning(
                "Atalho salvo invalido ({Saved}): usando o padrao {Default}",
                fellBackFrom.Trim().Length == 0 ? "(vazio)" : fellBackFrom.Trim(),
                text);
        }

        _applied = text;
        var display = Describe(gesture);
        var registered = TryRegister(gesture);
        var status = new HotkeyStatus(
            text,
            display,
            registered,
            registered
                ? null
                : $"O atalho {display} já é usado por outro programa. Use o ícone da bandeja ou escolha outro atalho em Configurações.",
            registered ? null : $"Atalho {display} em uso por outro programa",
            fellBackFrom);

        if (status == Status)
        {
            return;
        }

        Status = status;
        StatusChanged?.Invoke(status);
    }

    /// <summary>The hotkey as a Brazilian user reads it: <c>Ctrl+Alt+Espaco</c>.</summary>
    public static string Describe(HotkeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);

        return gesture.ToString().Replace("+Space", "+Espa\U000000E7o", StringComparison.Ordinal);
    }

    private bool TryRegister(HotkeyGesture gesture)
    {
        try
        {
            return _registrar.Register(gesture);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

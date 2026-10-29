using AlertaIBT.Core.Input;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Ndi;

/// <summary>Registers one global hotkey by id. The real one is on the app's message window; tests use a fake.</summary>
public interface INdiHotkeyRegistry
{
    /// <summary>Registers (modifiers, virtual key) under <paramref name="id"/>. False when another program owns the combination.</summary>
    bool Register(int id, HotkeyModifiers modifiers, int virtualKey);

    /// <summary>Gives the id back.</summary>
    void Unregister(int id);
}

/// <summary>One shortcut of the NDI tab.</summary>
/// <param name="Id">The registration id.</param>
/// <param name="Text">The shortcut as the user reads it (<c>F1</c>, <c>Ctrl+Espaco</c>).</param>
/// <param name="Description">What it does.</param>
/// <param name="Registered">Whether Windows accepted it.</param>
public sealed record NdiHotkeyInfo(int Id, string Text, string Description, bool Registered);

/// <summary>
/// The shortcuts of the NDI telao: <c>F1</c>-<c>F4</c> put camera 1-4 in Preview and <c>Ctrl+Space</c> is Take. They are registered only while the
/// setting is on (and given back when it goes off), and a combination another program owns becomes a warning, never an error. Note: F1-F4 are
/// plain keys, so while this is on they are taken away from any other program (Holyrics included) - the owner asked for them knowingly.
/// </summary>
public sealed class NdiHotkeyController : IDisposable
{
    /// <summary>The id of F1; F2..F4 follow (0xB21..0xB24).</summary>
    public const int FirstSelectId = 0xB21;

    /// <summary>The id of Ctrl+Space (Take).</summary>
    public const int TakeId = 0xB25;

    private const int VkF1 = 0x70;
    private const int VkSpace = 0x20;
    private const int SelectCount = 4;

    private readonly INdiHotkeyRegistry _registry;
    private readonly INdiControl _control;
    private readonly ILogger? _logger;
    private readonly List<NdiHotkeyInfo> _infos = [];
    private bool _enabled;

    /// <param name="registry">Registers with the system.</param>
    /// <param name="control">What the keys drive.</param>
    /// <param name="logger">Receives the conflicts.</param>
    public NdiHotkeyController(INdiHotkeyRegistry registry, INdiControl control, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(control);

        _registry = registry;
        _control = control;
        _logger = logger;
    }

    /// <summary>Raised when the list of shortcuts or their conflicts changed (after <see cref="Apply"/>).</summary>
    public event Action? Changed;

    /// <summary>The shortcuts registered now (empty while off).</summary>
    public IReadOnlyList<NdiHotkeyInfo> Hotkeys => _infos;

    /// <summary>One line per shortcut another program owns, in words for the user; empty when all work.</summary>
    public IReadOnlyList<string> Warnings =>
        _infos.Where(i => !i.Registered).Select(i => $"O atalho {i.Text} ({i.Description}) j\U000000E1 \U000000E9 usado por outro programa.").ToList();

    /// <summary>Whether the shortcuts are on.</summary>
    public bool Enabled => _enabled;

    /// <summary>Turns the shortcuts on or off (registering or giving them back).</summary>
    public void Apply(bool enabled)
    {
        if (enabled == _enabled)
        {
            return;
        }

        foreach (var info in _infos.Where(i => i.Registered))
        {
            _registry.Unregister(info.Id);
        }

        _infos.Clear();
        _enabled = enabled;
        if (enabled)
        {
            for (var i = 0; i < SelectCount; i++)
            {
                Add(FirstSelectId + i, HotkeyModifiers.None, VkF1 + i, $"F{i + 1}", $"c\U000000E2mera {i + 1} no Preview");
            }

            Add(TakeId, HotkeyModifiers.Control, VkSpace, "Ctrl+Espa\U000000E7o", "cortar (Take)");
            foreach (var warning in Warnings)
            {
                _logger?.LogWarning("NDI: {Warning}", warning);
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Tries again to register the shortcuts another program owned (it may have closed since).</summary>
    public void Retry()
    {
        if (_enabled && _infos.Any(i => !i.Registered))
        {
            _enabled = false;
            Apply(true);
        }
    }

    /// <summary>Handles a pressed hotkey id (returns false when it is not one of ours).</summary>
    public bool OnHotkey(int id)
    {
        if (!_enabled)
        {
            return false;
        }

        if (id is >= FirstSelectId and < FirstSelectId + SelectCount)
        {
            _control.SelectPreview(id - FirstSelectId);
            return true;
        }

        if (id == TakeId)
        {
            _control.Take();
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public void Dispose() => Apply(false);

    private void Add(int id, HotkeyModifiers modifiers, int key, string text, string description)
    {
        bool ok;
        try
        {
            ok = _registry.Register(id, modifiers, key);
        }
        catch (Exception)
        {
            ok = false;
        }

        _infos.Add(new NdiHotkeyInfo(id, text, description, ok));
    }
}

using AlertaIBT.App.Platform;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Control;
using AlertaIBT.Core.Holyrics;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.ViewModels;

/// <summary>A keyboard gate that never touches Windows: the test calls <see cref="Press"/> as if a key went through the hook.</summary>
internal sealed class FakeKeyboardGate : IKeyboardGate
{
    private KeyConsumer? _consumer;

    public bool IsInstalled { get; private set; }

    public bool AcceptInstall { get; set; } = true;

    public bool AcceptRefresh { get; set; } = true;

    public int Installs { get; private set; }

    public int Uninstalls { get; private set; }

    public int Refreshes { get; private set; }

    public bool Install(KeyConsumer consumer)
    {
        if (!AcceptInstall)
        {
            return false;
        }

        _consumer = consumer;
        IsInstalled = true;
        Installs++;
        return true;
    }

    public bool Refresh()
    {
        Refreshes++;
        return AcceptRefresh;
    }

    public void Uninstall()
    {
        if (IsInstalled)
        {
            Uninstalls++;
        }

        IsInstalled = false;
        _consumer = null;
    }

    public void Dispose() => Uninstall();

    /// <summary>The hook's answer for one key event: true when the key would be swallowed; false (passes) when no hook is installed.</summary>
    public bool Press(int virtualKey, bool down) => _consumer?.Invoke(virtualKey, down) ?? false;
}

/// <summary>A Holyrics that only records the commands it was given.</summary>
internal sealed class FakeHolyricsRemote : IHolyricsRemote
{
    private readonly object _gate = new();
    private readonly List<HolyricsCommand> _posted = [];

    public bool IsConfigured { get; set; } = true;

    public bool ArrowsActive => IsConfigured;

    public IReadOnlyList<HolyricsCommand> Posted
    {
        get
        {
            lock (_gate)
            {
                return _posted.ToList();
            }
        }
    }

    public bool Post(HolyricsCommand command)
    {
        lock (_gate)
        {
            _posted.Add(command);
        }

        return true;
    }

    /// <summary>Waits (up to 3 s) until <paramref name="count"/> commands arrived: the controller sends them from a background task.</summary>
    public bool WaitFor(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (Posted.Count >= count)
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return Posted.Count >= count;
    }
}

internal sealed class FakeRegistrar : IHotkeyRegistrar
{
    public bool Accept { get; set; } = true;

    public List<string> Registered { get; } = [];

    public bool Register(AlertaIBT.Core.Input.HotkeyGesture gesture)
    {
        Registered.Add(gesture.ToString());
        return Accept;
    }

    public void Unregister()
    {
    }
}

/// <summary>Real settings and the real presentation controller over a fake hook and a fake Holyrics, plus the Controle view-model that shows them.</summary>
internal sealed class PresentationRig : IDisposable
{
    public PresentationRig(AppSettings? initial = null, bool configured = true, bool panelFocused = false)
    {
        Chrome = new SettingsRig(initial);
        Remote = new FakeHolyricsRemote { IsConfigured = configured };
        PanelFocused = panelFocused;
        Controller = new PresentationModeController(
            Chrome.Settings,
            Remote,
            Gate,
            work => work(),
            panelHandlesKey: key => PanelFocused && key is 0x25 or 0x27,
            modifierHeld: () => ModifierHeld,
            clock: Chrome.Clock);
        Registrar = new FakeRegistrar();
        Controller.Start(Registrar);
        Service = new ControlService(Chrome.Settings, new RecordingTargets(), () => new FakeArtNet(), clock: Chrome.Clock);
        Vm = new ControlViewModel(Chrome.Settings, Service, (_, _) => true, protector: Chrome.Protector, presentation: Controller);
    }

    public SettingsRig Chrome { get; }

    public FakeHolyricsRemote Remote { get; }

    public FakeKeyboardGate Gate { get; } = new();

    public FakeRegistrar Registrar { get; }

    public PresentationModeController Controller { get; }

    public ControlService Service { get; }

    public ControlViewModel Vm { get; }

    /// <summary>The text box of the quick panel has the focus (and the arrows switch is on): the panel itself handles Left and Right.</summary>
    public bool PanelFocused { get; set; }

    public bool ModifierHeld { get; set; }

    public void Dispose()
    {
        Vm.Dispose();
        Controller.Dispose();
        Service.Dispose();
        Chrome.Dispose();
    }
}

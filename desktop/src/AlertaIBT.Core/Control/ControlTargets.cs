using AlertaIBT.Core.Holyrics;
using AlertaIBT.Core.Ndi;
using AlertaIBT.Core.Player;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Control;

/// <summary>The NDI side of the control: the same calls the buttons, the shortcuts and the API make.</summary>
public interface IControlNdi
{
    /// <summary>Puts camera <paramref name="slot"/> (0-based) in Preview.</summary>
    ControlResult SelectPreview(int slot);

    /// <summary>Cuts Preview to Program.</summary>
    ControlResult Take();

    /// <summary>Puts the camera layer on the telao ON or OFF.</summary>
    ControlResult SetLayer(bool on);

    /// <summary>Whether the layer is ON now.</summary>
    bool LayerOn { get; }

    /// <summary>The slot (0-based) in Preview, or null.</summary>
    int? PreviewSlot { get; }

    /// <summary>The slot (0-based) on air, or null.</summary>
    int? ProgramSlot { get; }

    /// <summary>The vertical position (0 to 100) of a slot.</summary>
    int GetVerticalPosition(int slot);

    /// <summary>Moves a slot's picture (0 to 100); it moves at once and is saved.</summary>
    ControlResult SetVerticalPosition(int slot, int position);
}

/// <summary>The volumes of the "Som" tab, reached through the same controls the sliders use (live, honoring the volume ramp setting).</summary>
public interface IControlVolumes
{
    /// <summary>The volume (0 to 100) the app holds now, or null when the volume is not available.</summary>
    double? Get(ControlAction action);

    /// <summary>Sets the volume (0 to 100) exactly as the slider does; hands over to the UI thread by itself and returns at once.</summary>
    void Set(ControlAction action, int volume);
}

/// <summary><see cref="IControlNdi"/> over the real <see cref="NdiService"/>.</summary>
public sealed class NdiControlAdapter(NdiService ndi) : IControlNdi
{
    /// <inheritdoc />
    public bool LayerOn => ndi.LayerOn;

    /// <inheritdoc />
    public int? PreviewSlot => ndi.GetStatus().Switcher.PreviewSlot;

    /// <inheritdoc />
    public int? ProgramSlot => ndi.GetStatus().Switcher.ProgramSlot;

    /// <inheritdoc />
    public ControlResult SelectPreview(int slot) => Convert(ndi.SelectPreview(slot));

    /// <inheritdoc />
    public ControlResult Take() => Convert(ndi.Take());

    /// <inheritdoc />
    public ControlResult SetLayer(bool on) => Convert(ndi.SetLayer(on));

    /// <inheritdoc />
    public int GetVerticalPosition(int slot) =>
        slot is >= 0 and < NdiSettings.SlotCount && slot < ndi.Settings.Cameras.Count ? ndi.Settings.Cameras[slot].VerticalPosition : NdiSettings.DefaultVerticalPosition;

    /// <inheritdoc />
    public ControlResult SetVerticalPosition(int slot, int position) => Convert(ndi.SetVerticalPosition(slot, position));

    private static ControlResult Convert(NdiResult result) => result.Success ? ControlResult.Ok : ControlResult.Fail(result.Message ?? "falhou");
}

/// <summary>
/// What the actions of the "Controle" map really do: each one goes through the same controller the buttons use (the NDI service, the player, the volume
/// rows), never around it. The Holyrics actions are queued on <see cref="IHolyricsRemote"/> and sent in the background (they never wait for Holyrics).
/// </summary>
public sealed class ControlTargets : IControlTargets
{
    /// <summary>The text of a Holyrics action when the connection to Holyrics is not filled in.</summary>
    public const string HolyricsMissingText = "Holyrics sem token (aba Controle)";

    private readonly IControlNdi _ndi;
    private readonly IPlayerControl _player;
    private readonly IControlVolumes _volumes;
    private readonly IHolyricsRemote? _holyrics;

    /// <param name="ndi">The NDI engine.</param>
    /// <param name="player">The music player.</param>
    /// <param name="volumes">The volume controls.</param>
    /// <param name="holyrics">The Holyrics connection; without one the Holyrics actions fail with a clear message.</param>
    public ControlTargets(IControlNdi ndi, IPlayerControl player, IControlVolumes volumes, IHolyricsRemote? holyrics = null)
    {
        ArgumentNullException.ThrowIfNull(ndi);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(volumes);

        _ndi = ndi;
        _player = player;
        _volumes = volumes;
        _holyrics = holyrics;
    }

    /// <inheritdoc />
    public ControlResult Run(ControlCommand command)
    {
        switch (command.Action)
        {
            case ControlAction.SelectCamera:
                return command.Parameter is >= 1 and <= NdiSettings.SlotCount
                    ? _ndi.SelectPreview(command.Parameter - 1)
                    : ControlResult.Fail("c\U000000E2mera inexistente");

            case ControlAction.CameraOnTelao:
                return _ndi.SetLayer(command.Kind == ControlCommandKind.SetState ? command.On : !_ndi.LayerOn);

            case ControlAction.Take:
                return _ndi.Take();

            case ControlAction.PlayPause:
                var status = _player.GetStatus();
                return Convert(status.IsPlaying && !status.Fading ? _player.Pause() : _player.Play());

            case ControlAction.Stop:
                return Convert(_player.Stop());

            case ControlAction.Next:
                return Convert(_player.Next());

            case ControlAction.Previous:
                return Convert(_player.Previous());

            case ControlAction.PreviewVertical:
                return SetVertical(_ndi.PreviewSlot, command.Value, "sem c\U000000E2mera na pr\U000000E9via");

            case ControlAction.ProgramVertical:
                return SetVertical(_ndi.ProgramSlot, command.Value, "sem c\U000000E2mera no ar");

            case ControlAction.VolumeWindows:
            case ControlAction.VolumeHolyrics:
            case ControlAction.VolumePlayer:
                _volumes.Set(command.Action, (int)Math.Round(Math.Clamp(command.Value, 0, 100)));
                return ControlResult.Ok;

            case ControlAction.SlideNext:
                return Holyrics(new HolyricsCommand(HolyricsActionKind.SlideNext));

            case ControlAction.SlidePrevious:
                return Holyrics(new HolyricsCommand(HolyricsActionKind.SlidePrevious));

            case ControlAction.VideoPlayPause:
                return Holyrics(new HolyricsCommand(HolyricsActionKind.VideoPlayPause));

            case ControlAction.LogoToggle:
                // "Estado": the channel is the logo (ON shows, OFF hides). "Botao": every change flips the app's own memory of the logo.
                return Holyrics(command.Kind == ControlCommandKind.SetState
                    ? new HolyricsCommand(HolyricsActionKind.LogoSet, command.On)
                    : new HolyricsCommand(HolyricsActionKind.LogoToggle));

            default:
                return ControlResult.Fail("a\U000000E7\U000000E3o desconhecida");
        }
    }

    /// <inheritdoc />
    public double? GetLevel(ControlAction action) => action switch
    {
        ControlAction.PreviewVertical => _ndi.PreviewSlot is { } slot ? _ndi.GetVerticalPosition(slot) : null,
        ControlAction.ProgramVertical => _ndi.ProgramSlot is { } slot ? _ndi.GetVerticalPosition(slot) : null,
        ControlAction.VolumeWindows or ControlAction.VolumeHolyrics or ControlAction.VolumePlayer => _volumes.Get(action),
        _ => null,
    };

    // Queued and sent in the background: the answer of Holyrics (or its silence) never holds up the next command of the console.
    private ControlResult Holyrics(HolyricsCommand command) =>
        _holyrics is { IsConfigured: true } remote && remote.Post(command) ? ControlResult.Ok : ControlResult.Fail(HolyricsMissingText);

    private ControlResult SetVertical(int? slot, double value, string missing) =>
        slot is { } s ? _ndi.SetVerticalPosition(s, (int)Math.Round(Math.Clamp(value, 0, 100))) : ControlResult.Fail(missing);

    private static ControlResult Convert(PlayerResult result) => result.Success ? ControlResult.Ok : ControlResult.Fail(result.Error ?? "falhou");
}

namespace AlertaIBT.Core.Control;

/// <summary>What a channel of the console can do in the app. The text form (<see cref="ControlCatalog.ToText"/>) is what <c>settings.json</c> holds.</summary>
public enum ControlAction
{
    /// <summary>Puts camera N (the row's parameter, 1 to 6) in Preview.</summary>
    SelectCamera,

    /// <summary>Puts the camera layer on the telao ON or OFF.</summary>
    CameraOnTelao,

    /// <summary>Cuts Preview to Program (Cortar).</summary>
    Take,

    /// <summary>Plays when paused or stopped, pauses when playing.</summary>
    PlayPause,

    /// <summary>Stops the music.</summary>
    Stop,

    /// <summary>The next track (Avancar).</summary>
    Next,

    /// <summary>The previous track (Voltar).</summary>
    Previous,

    /// <summary>The next slide in Holyrics.</summary>
    SlideNext,

    /// <summary>The previous slide in Holyrics.</summary>
    SlidePrevious,

    /// <summary>Plays or pauses the video of Holyrics.</summary>
    VideoPlayPause,

    /// <summary>Shows or hides the logo of Holyrics.</summary>
    LogoToggle,

    /// <summary>The vertical position (0 to 100 %) of the camera that is in Preview.</summary>
    PreviewVertical,

    /// <summary>The vertical position (0 to 100 %) of the camera that is on air.</summary>
    ProgramVertical,

    /// <summary>The Windows master volume.</summary>
    VolumeWindows,

    /// <summary>The Holyrics volume.</summary>
    VolumeHolyrics,

    /// <summary>The player volume.</summary>
    VolumePlayer,
}

/// <summary>How a channel is read.</summary>
public enum ControlMode
{
    /// <summary>A digital channel (value 128 or more is ON): the action runs when the channel changes (see <see cref="ControlTrigger"/>).</summary>
    Button,

    /// <summary>Selects a camera on the rising edge (OFF to ON).</summary>
    Select,

    /// <summary>The action follows the channel: ON means on, OFF means off.</summary>
    State,

    /// <summary>An analog channel: 0 to 255 becomes 0 to 100 %, with a soft pickup.</summary>
    Fader,
}

/// <summary>When a <see cref="ControlMode.Button"/> row runs its action.</summary>
public enum ControlTrigger
{
    /// <summary>Whenever the channel changes between OFF and ON (the owner's macros hold the channel ON or OFF).</summary>
    AnyChange,

    /// <summary>Only when the channel goes from OFF to ON.</summary>
    RisingOnly,
}

/// <summary>What the catalog knows about one action.</summary>
/// <param name="Action">The action.</param>
/// <param name="Name">The name shown on the screen (pt-BR).</param>
/// <param name="IconKey">The resource key of the Lucide icon.</param>
/// <param name="Modes">The modes the action accepts; the first is the default.</param>
/// <param name="Available">False for an action whose engine does not exist (none today): its row is shown but never runs.</param>
public sealed record ControlActionInfo(ControlAction Action, string Name, string IconKey, IReadOnlyList<ControlMode> Modes, bool Available)
{
    /// <summary>The mode a new row of this action gets.</summary>
    public ControlMode DefaultMode => Modes[0];

    /// <summary>Whether this action is an analog one (a fader).</summary>
    public bool IsFader => Modes.Count == 1 && Modes[0] == ControlMode.Fader;
}

/// <summary>The list of actions and their text forms.</summary>
public static class ControlCatalog
{
    private static readonly ControlMode[] ButtonOnly = [ControlMode.Button];
    private static readonly ControlMode[] ButtonOrState = [ControlMode.Button, ControlMode.State];
    private static readonly ControlMode[] StateOrButton = [ControlMode.State, ControlMode.Button];
    private static readonly ControlMode[] SelectOnly = [ControlMode.Select];
    private static readonly ControlMode[] FaderOnly = [ControlMode.Fader];

    private static readonly ControlActionInfo[] Items =
    [
        new(ControlAction.SelectCamera, "C\U000000E2mera na pr\U000000E9via", "LucideVideo", SelectOnly, true),
        new(ControlAction.CameraOnTelao, "C\U000000E2mera no tel\U000000E3o", "LucideMonitorPlay", StateOrButton, true),
        new(ControlAction.Take, "Cortar", "LucideScissors", ButtonOnly, true),
        new(ControlAction.PlayPause, "Tocar ou pausar a m\U000000FAsica", "LucidePlay", ButtonOnly, true),
        new(ControlAction.Stop, "Parar a m\U000000FAsica", "LucideSquare", ButtonOnly, true),
        new(ControlAction.Next, "Avan\U000000E7ar a m\U000000FAsica", "LucideSkipForward", ButtonOnly, true),
        new(ControlAction.Previous, "Voltar a m\U000000FAsica", "LucideSkipBack", ButtonOnly, true),
        new(ControlAction.SlideNext, "Slide pr\U000000F3ximo", "LucidePresentation", ButtonOnly, true),
        new(ControlAction.SlidePrevious, "Slide anterior", "LucidePresentation", ButtonOnly, true),
        new(ControlAction.VideoPlayPause, "Play/pausa do v\U000000EDdeo do Holyrics", "LucideVideo", ButtonOnly, true),
        new(ControlAction.LogoToggle, "Logo do Holyrics", "LucideEye", ButtonOrState, true),
        new(ControlAction.PreviewVertical, "Posi\U000000E7\U000000E3o da pr\U000000E9via", "LucideMoveVertical", FaderOnly, true),
        new(ControlAction.ProgramVertical, "Posi\U000000E7\U000000E3o do no ar", "LucideMoveVertical", FaderOnly, true),
        new(ControlAction.VolumeWindows, "Volume do Windows", "LucideVolume2", FaderOnly, true),
        new(ControlAction.VolumeHolyrics, "Volume do Holyrics", "LucideVolume2", FaderOnly, true),
        new(ControlAction.VolumePlayer, "Volume do player", "LucideMusic", FaderOnly, true),
    ];

    /// <summary>Every action, in the order the pick lists show them.</summary>
    public static IReadOnlyList<ControlActionInfo> All => Items;

    /// <summary>What the catalog knows about <paramref name="action"/>.</summary>
    public static ControlActionInfo Get(ControlAction action) => Items.First(item => item.Action == action);

    /// <summary>The text of <paramref name="action"/> in <c>settings.json</c> (camelCase).</summary>
    public static string ToText(ControlAction action) => char.ToLowerInvariant(action.ToString()[0]) + action.ToString()[1..];

    /// <summary>The action named <paramref name="text"/>, or <c>null</c> when it is not one.</summary>
    public static ControlAction? ParseAction(string? text)
    {
        foreach (var item in Items)
        {
            if (string.Equals(ToText(item.Action), text?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return item.Action;
            }
        }

        return null;
    }

    /// <summary>The mode named <paramref name="text"/>, or <c>null</c>.</summary>
    public static ControlMode? ParseMode(string? text) =>
        Enum.TryParse<ControlMode>(text?.Trim(), true, out var mode) && Enum.IsDefined(mode) ? mode : null;

    /// <summary>The trigger named <paramref name="text"/>, or <c>null</c>.</summary>
    public static ControlTrigger? ParseTrigger(string? text) =>
        Enum.TryParse<ControlTrigger>(text?.Trim(), true, out var trigger) && Enum.IsDefined(trigger) ? trigger : null;

    /// <summary>The text of a mode or trigger in <c>settings.json</c> (camelCase).</summary>
    public static string ToText(Enum value) => char.ToLowerInvariant(value.ToString()[0]) + value.ToString()[1..];
}

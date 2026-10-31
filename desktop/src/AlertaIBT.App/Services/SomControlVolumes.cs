using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Control;

namespace AlertaIBT.App.Services;

/// <summary>
/// The volumes the console faders move: the very rows of the "Som" tab, so a fader does exactly what the slider does (the live volume at once,
/// the volume ramp setting, the autosave) and the slider on the screen follows it. The rows belong to the UI thread: a write is handed over to it,
/// a read is a plain number.
/// </summary>
public sealed class SomControlVolumes : IControlVolumes
{
    private readonly SomViewModel _som;
    private readonly Action<Action> _post;

    /// <param name="som">The Som tab.</param>
    /// <param name="post">Runs an action on the UI thread.</param>
    public SomControlVolumes(SomViewModel som, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(som);
        ArgumentNullException.ThrowIfNull(post);

        _som = som;
        _post = post;
    }

    /// <inheritdoc />
    public double? Get(ControlAction action) => Row(action)?.Volume;

    /// <inheritdoc />
    public void Set(ControlAction action, int volume)
    {
        if (Row(action) is { } row)
        {
            _post(() => row.Volume = volume);
        }
    }

    private VolumeRow? Row(ControlAction action) => action switch
    {
        ControlAction.VolumeWindows => _som.MasterRow,
        ControlAction.VolumeHolyrics => _som.HolyricsRow,
        ControlAction.VolumePlayer => _som.PlayerRow,
        _ => null,
    };
}

using AlertaIBT.Core.Control;

namespace AlertaIBT.Core.Settings;

/// <summary>
/// One line of the map of the "Controle" tab: which channel of the console runs which action, and how the channel is read.
/// </summary>
/// <param name="Channel">The DMX channel, 1 to 512.</param>
/// <param name="Action">What it does.</param>
/// <param name="Mode">How the channel is read (see <see cref="ControlCatalog.Get"/> for what an action accepts).</param>
/// <param name="Trigger">For a <see cref="ControlMode.Button"/>: every change, or only the rising edge.</param>
/// <param name="Parameter">The camera (1 to 6) of a <see cref="ControlAction.SelectCamera"/> row; 0 for the others.</param>
/// <param name="Pickup">For a fader: the app ignores it until it crosses the app's own value (so the value never jumps).</param>
/// <param name="Enabled">Whether the row is active.</param>
public sealed record ControlRow(
    int Channel,
    ControlAction Action,
    ControlMode Mode,
    ControlTrigger Trigger = ControlTrigger.AnyChange,
    int Parameter = 0,
    bool Pickup = true,
    bool Enabled = true);

/// <summary>
/// The "Controle" tab: the app is controlled by a grandMA2 (onPC) sending Art-Net on the network. Every field is optional in <c>settings.json</c>:
/// an older file loads with the defaults. The app only RECEIVES Art-Net (never sends). Not part of the backup.
/// </summary>
public sealed record ControlSettings
{
    /// <summary>Highest channel of a universe.</summary>
    public const int ChannelCount = 512;

    /// <summary>How many rows the map may have.</summary>
    public const int MaxRows = 128;

    /// <summary>The universe the owner uses (as shown on the console).</summary>
    public const int DefaultUniverse = 100;

    /// <summary>The Art-Net port.</summary>
    public const int ArtNetPort = 6454;

    /// <summary>The master switch "Controle pela MA2": off at install, and while off the socket is closed.</summary>
    public bool Enabled { get; init; }

    /// <summary>The universe as the console shows it (see <see cref="UniverseOffset"/>).</summary>
    public int Universe { get; init; } = DefaultUniverse;

    /// <summary>
    /// "Universo mostrado na MA2 = Art-Net + 1": the grandMA2 counts universes from 1 on screen, Art-Net from 0 on the wire. On by default, so a console
    /// universe of 100 is the Art-Net universe 99.
    /// </summary>
    public bool UniverseOffset { get; init; } = true;

    /// <summary>"IP da MA2": when set, packets from other addresses are dropped; blank accepts any source.</summary>
    public string SourceIp { get; init; } = string.Empty;

    /// <summary>The map, one row per action.</summary>
    public IReadOnlyList<ControlRow> Rows { get; init; } = DefaultRows();

    /// <summary>The universe on the wire (0 to 32767): <see cref="Universe"/> minus one when <see cref="UniverseOffset"/> is on.</summary>
    public int ArtNetUniverse => Math.Clamp(Universe - (UniverseOffset ? 1 : 0), 0, 32767);

    /// <summary>The lowest universe the field accepts.</summary>
    public int MinUniverse => UniverseOffset ? 1 : 0;

    /// <summary>The highest universe the field accepts.</summary>
    public int MaxUniverse => UniverseOffset ? 32768 : 32767;

    /// <summary>The owner's 21 channels: cameras 1-6, camera on the telao, Cortar, the player keys, 4 Holyrics rows (slides, video, logo), 5 faders.</summary>
    public static IReadOnlyList<ControlRow> DefaultRows()
    {
        var rows = new List<ControlRow>();
        for (var camera = 1; camera <= 6; camera++)
        {
            rows.Add(new ControlRow(camera, ControlAction.SelectCamera, ControlMode.Select, ControlTrigger.AnyChange, camera));
        }

        rows.Add(new ControlRow(7, ControlAction.CameraOnTelao, ControlMode.State));
        rows.Add(new ControlRow(8, ControlAction.Take, ControlMode.Button));
        rows.Add(new ControlRow(9, ControlAction.PlayPause, ControlMode.Button));
        rows.Add(new ControlRow(10, ControlAction.Stop, ControlMode.Button));
        rows.Add(new ControlRow(11, ControlAction.Next, ControlMode.Button));
        rows.Add(new ControlRow(12, ControlAction.Previous, ControlMode.Button));
        rows.Add(new ControlRow(13, ControlAction.SlideNext, ControlMode.Button));
        rows.Add(new ControlRow(14, ControlAction.SlidePrevious, ControlMode.Button));
        rows.Add(new ControlRow(15, ControlAction.VideoPlayPause, ControlMode.Button));
        rows.Add(new ControlRow(16, ControlAction.LogoToggle, ControlMode.Button));
        rows.Add(new ControlRow(17, ControlAction.PreviewVertical, ControlMode.Fader));
        rows.Add(new ControlRow(18, ControlAction.ProgramVertical, ControlMode.Fader));
        rows.Add(new ControlRow(19, ControlAction.VolumeWindows, ControlMode.Fader));
        rows.Add(new ControlRow(20, ControlAction.VolumeHolyrics, ControlMode.Fader));
        rows.Add(new ControlRow(21, ControlAction.VolumePlayer, ControlMode.Fader));
        return rows;
    }

    /// <summary>Value equality with the rows compared one by one.</summary>
    public bool Equals(ControlSettings? other) =>
        other is not null
        && Enabled == other.Enabled
        && Universe == other.Universe
        && UniverseOffset == other.UniverseOffset
        && SourceIp == other.SourceIp
        && Rows.SequenceEqual(other.Rows);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Enabled, Universe, UniverseOffset, SourceIp, Rows.Count);
}

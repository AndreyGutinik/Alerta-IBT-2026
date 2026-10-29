namespace AlertaIBT.Core.Settings;

/// <summary>One camera slot of the NDI telao: a name for the buttons, where to find it, and how its picture is cropped.</summary>
/// <param name="Name">The name on the buttons (<c>Camera 1</c> by default).</param>
/// <param name="Source">The NDI source name as the finder lists it (<c>MACHINE (Camera)</c>), or a direct address (<c>192.168.1.50</c>, <c>192.168.1.50:5961</c>); blank when the slot is empty.</param>
/// <param name="VerticalPosition">Which band of the picture shows on the LED: 0 is the top of the picture, 100 the bottom, 50 the middle.</param>
public sealed record NdiCameraSlot(string Name, string Source, int VerticalPosition = NdiSettings.DefaultVerticalPosition);

/// <summary>
/// The NDI -> telao feature (receive only). Every field is optional in <c>settings.json</c>: an older file loads with the defaults. The layer
/// itself is never saved: it is always OFF when the app starts.
/// </summary>
public sealed record NdiSettings
{
    /// <summary>How many camera slots there are.</summary>
    public const int SlotCount = 6;

    /// <summary>The vertical position of a new slot.</summary>
    public const int DefaultVerticalPosition = 50;

    /// <summary>Whether the keyboard shortcuts (F1-F4 select the camera into Preview, Ctrl+Space is Take) are registered. On by default.</summary>
    public bool ShortcutsEnabled { get; init; } = true;

    /// <summary>IP addresses (or host names) the finder also asks for sources, for cameras on another subnet.</summary>
    public IReadOnlyList<string> FixedIps { get; init; } = [];

    /// <summary>The six camera slots, always exactly <see cref="SlotCount"/> after normalizing.</summary>
    public IReadOnlyList<NdiCameraSlot> Cameras { get; init; } = DefaultCameras();

    /// <summary>When true (default) the camera goes to the 2560x768 LED monitor; the owner may unlock another monitor.</summary>
    public bool MonitorLocked { get; init; } = true;

    /// <summary>The display name (<c>\.\DISPLAY2</c>) of the monitor chosen while <see cref="MonitorLocked"/> is false; blank for none.</summary>
    public string MonitorDeviceName { get; init; } = string.Empty;

    /// <summary>"Esconder da live": the camera window is excluded from screen capture (OBS, NDI). Off by default (OBS may want to see it).</summary>
    public bool HideFromCapture { get; init; }

    /// <summary>The six default slots: <c>Camera 1</c> .. <c>Camera 6</c>, empty, centered.</summary>
    public static IReadOnlyList<NdiCameraSlot> DefaultCameras() =>
        Enumerable.Range(1, SlotCount).Select(n => new NdiCameraSlot($"C\U000000E2mera {n}", string.Empty)).ToList();

    /// <summary>Value equality with the lists compared item by item.</summary>
    public bool Equals(NdiSettings? other) =>
        other is not null
        && ShortcutsEnabled == other.ShortcutsEnabled
        && MonitorLocked == other.MonitorLocked
        && MonitorDeviceName == other.MonitorDeviceName
        && HideFromCapture == other.HideFromCapture
        && FixedIps.SequenceEqual(other.FixedIps)
        && Cameras.SequenceEqual(other.Cameras);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(ShortcutsEnabled, MonitorLocked, MonitorDeviceName, HideFromCapture, FixedIps.Count, Cameras.Count);
}

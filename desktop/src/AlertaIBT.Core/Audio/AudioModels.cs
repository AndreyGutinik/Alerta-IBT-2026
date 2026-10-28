using System.Text.RegularExpressions;

namespace AlertaIBT.Core.Audio;

/// <summary>An active output (render) endpoint of Windows: its id and the name the Sound panel shows.</summary>
/// <param name="Id">The endpoint id (<c>{0.0.0.00000000}.{guid}</c>).</param>
/// <param name="Name">The friendly name, such as <c>Alto-falantes (WING USB)</c>.</param>
public sealed record AudioDevice(string Id, string Name)
{
    /// <summary>The short name for lines of text: what is inside the last parentheses (without the "2- " Windows adds to a second device of the same kind), else the whole name.</summary>
    public string ShortName => AudioNames.Short(Name);
}

/// <summary>One audio session (a program, or the Windows system sounds) on an output.</summary>
/// <param name="Key">Identifies this session instance on its device (it changes when the program is closed and opened again).</param>
/// <param name="ProcessName">The program's process name without <c>.exe</c>; <c>null</c> for the system sounds or when the process is gone.</param>
/// <param name="IsSystemSounds">Whether this is the "Sons do sistema" session.</param>
/// <param name="ProcessId">The process id, or 0 when unknown (the system sounds, or a process that is gone).</param>
public sealed record AudioSessionInfo(string Key, string? ProcessName, bool IsSystemSounds, int ProcessId = 0);

/// <summary>The default output for each of the three roles of Windows (any can be missing).</summary>
public sealed record DefaultDevices(string? Console, string? Multimedia, string? Communications)
{
    /// <summary>Whether all three roles point at <paramref name="deviceId"/>.</summary>
    public bool AllEqual(string deviceId) =>
        string.Equals(Console, deviceId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Multimedia, deviceId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Communications, deviceId, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The Windows audio, as thin as it can be, so every rule lives in <see cref="AudioController"/> and is tested against a fake. Every
/// method may be called from any non-UI thread, never throws for a missing device (returns false or an empty list), and the three
/// events may be raised on any thread: the listener must only schedule work, never call back into this object from inside them.
/// </summary>
public interface IAudioSystem : IDisposable
{
    /// <summary>Raised when a device appeared, went away or changed state.</summary>
    event Action? DevicesChanged;

    /// <summary>Raised when the default output of any role changed.</summary>
    event Action? DefaultDeviceChanged;

    /// <summary>Raised when a new audio session appeared on the output given to <see cref="WatchSessions"/>.</summary>
    event Action? SessionCreated;

    /// <summary>The active output devices.</summary>
    IReadOnlyList<AudioDevice> ActiveRenderDevices();

    /// <summary>The default output of each role.</summary>
    DefaultDevices GetDefaultDevices();

    /// <summary>Makes <paramref name="deviceId"/> the default output for the console, multimedia and communications roles.</summary>
    /// <returns>Whether Windows accepted it.</returns>
    bool SetDefaultRenderDevice(string deviceId);

    /// <summary>Sets the master volume (0 to 100) and the mute of an output.</summary>
    bool SetMasterVolume(string deviceId, int percent, bool muted);

    /// <summary>The audio sessions that exist now on an output.</summary>
    IReadOnlyList<AudioSessionInfo> GetSessions(string deviceId);

    /// <summary>Sets the volume (0 to 100) and the mute of one session.</summary>
    bool SetSessionVolume(string deviceId, string sessionKey, int percent, bool muted);

    /// <summary>Starts telling (through <see cref="SessionCreated"/>) about new sessions on these outputs; an empty list stops.</summary>
    void WatchSessions(IReadOnlyList<string> deviceIds);
}

/// <summary>Name rules shared by the controller and the screen: process names and device names as the user and Windows write them.</summary>
public static partial class AudioNames
{
    /// <summary>Whether <paramref name="processName"/> is one of <paramref name="names"/>: case does not matter and a trailing <c>.exe</c> is ignored on both sides.</summary>
    public static bool MatchesProcess(IEnumerable<string> names, string? processName)
    {
        ArgumentNullException.ThrowIfNull(names);

        var wanted = StripExe(processName);
        return wanted.Length > 0 && names.Any(name => string.Equals(StripExe(name), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The device name as compared: lower case, spaces squeezed, and without the "2- " Windows puts in front of a second device of the same kind.</summary>
    public static string NormalizeDevice(string? name)
    {
        var text = DuplicatePrefix().Replace(name ?? string.Empty, "(");
        return WhiteSpace().Replace(text, " ").Trim().ToLowerInvariant();
    }

    /// <summary>The short name of a device (see <see cref="AudioDevice.ShortName"/>).</summary>
    public static string Short(string? name)
    {
        var text = (name ?? string.Empty).Trim();
        var open = text.LastIndexOf('(');
        var close = text.LastIndexOf(')');
        var inner = open >= 0 && close > open ? text[(open + 1)..close] : text;
        var shortName = DuplicatePrefix().Replace("(" + inner, "(")[1..].Trim();
        return shortName.Length > 0 ? shortName : text;
    }

    /// <summary>Picks the output the lock refers to: by endpoint id first, then by name (so the same device on another USB port still matches); <c>null</c> when it is not among <paramref name="active"/>.</summary>
    public static AudioDevice? Resolve(IReadOnlyList<AudioDevice> active, string? id, string? name)
    {
        ArgumentNullException.ThrowIfNull(active);

        if (!string.IsNullOrWhiteSpace(id)
            && active.FirstOrDefault(d => string.Equals(d.Id, id.Trim(), StringComparison.OrdinalIgnoreCase)) is { } byId)
        {
            return byId;
        }

        var wanted = NormalizeDevice(name);
        return wanted.Length == 0 ? null : active.FirstOrDefault(d => NormalizeDevice(d.Name) == wanted);
    }

    /// <summary>The first active output whose name contains <paramref name="text"/> (case does not matter), or <c>null</c>.</summary>
    public static AudioDevice? FindContaining(IReadOnlyList<AudioDevice> active, string text)
    {
        ArgumentNullException.ThrowIfNull(active);

        return string.IsNullOrWhiteSpace(text)
            ? null
            : active.FirstOrDefault(d => d.Name.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private static string StripExe(string? name)
    {
        var text = (name ?? string.Empty).Trim();
        return text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? text[..^4].TrimEnd() : text;
    }

    [GeneratedRegex(@"\(\s*\d+-\s*")]
    private static partial Regex DuplicatePrefix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}

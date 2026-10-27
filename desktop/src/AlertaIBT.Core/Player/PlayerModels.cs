using AlertaIBT.Core.Audio;

namespace AlertaIBT.Core.Player;

/// <summary>What the player is doing.</summary>
public enum PlayerState
{
    /// <summary>Nothing is playing and there is no current track.</summary>
    Stopped,

    /// <summary>A track is playing (it may be fading in or out: see <see cref="PlayerStatus.Fading"/>).</summary>
    Playing,

    /// <summary>Paused: the output is closed. "Play" then starts the next random track, not the same position.</summary>
    Paused,

    /// <summary>It should be playing but there is no usable output right now: it retries every few seconds by itself.</summary>
    Waiting,
}

/// <summary>What happens at the end of a track.</summary>
public enum PlayerRepeat
{
    /// <summary>Stop after the last track (a shuffled round ends when every track played once).</summary>
    Off,

    /// <summary>Start again from the top (a new shuffled round) without ever stopping.</summary>
    All,

    /// <summary>Play the same track again.</summary>
    One,
}

/// <summary>Text forms of <see cref="PlayerRepeat"/>, as saved in the settings and sent by the API.</summary>
public static class PlayerRepeatText
{
    /// <summary>The text of a mode: <c>off</c>, <c>all</c> or <c>one</c>.</summary>
    public static string ToText(PlayerRepeat repeat) => repeat switch { PlayerRepeat.Off => "off", PlayerRepeat.One => "one", _ => "all" };

    /// <summary>The mode of a text; anything unknown is <see cref="PlayerRepeat.All"/>.</summary>
    public static PlayerRepeat Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "off" => PlayerRepeat.Off,
        "one" => PlayerRepeat.One,
        _ => PlayerRepeat.All,
    };
}

/// <summary>A track as the screen and the API show it.</summary>
/// <param name="Path">The file path.</param>
/// <param name="FileName">The file name with extension.</param>
/// <param name="Title">The title (from the file tags, else from the file name).</param>
/// <param name="Artist">The artist, or <c>null</c> when unknown.</param>
public sealed record TrackInfo(string Path, string FileName, string Title, string? Artist);

/// <summary>The answer of a player command.</summary>
/// <param name="Success">Whether it was carried out (or was already true).</param>
/// <param name="Error">Why not, in Portuguese, when <paramref name="Success"/> is false.</param>
public sealed record PlayerResult(bool Success, string? Error = null)
{
    /// <summary>The text of the answer while the master switch is off.</summary>
    public const string DisabledText = "player desligado";

    /// <summary>It worked.</summary>
    public static PlayerResult Ok { get; } = new(true);

    /// <summary>The master switch "Player ligado" is off.</summary>
    public static PlayerResult Disabled { get; } = new(false, DisabledText);

    /// <summary>A refusal with a reason.</summary>
    public static PlayerResult Fail(string error) => new(false, error);
}

/// <summary>Where the player's sound goes right now and why: the text for the screen and how the device was found.</summary>
/// <param name="Text">A line such as <c>Sa\U000000EDda do player: WING-USB (Out 3-4)</c>, or that the wished device is absent and the default output stands in.</param>
/// <param name="Kind">How the output was chosen.</param>
/// <param name="DeviceName">The name of the device in use (or about to be used), or <c>null</c> when the Windows default stands in without a known name.</param>
public sealed record OutputStatus(string Text, OutputChoiceKind Kind, string? DeviceName)
{
    /// <summary>Nothing looked at yet.</summary>
    public static OutputStatus Unknown { get; } = new("Sa\U000000EDda do player: ainda n\U000000E3o verificada", OutputChoiceKind.None, null);
}

/// <summary>An immutable picture of the player, for the screen and for <c>/player/status</c>.</summary>
public sealed record PlayerStatus(
    bool Enabled,
    PlayerState State,
    bool Fading,
    TrackInfo? Track,
    TimeSpan Position,
    TimeSpan Duration,
    string Playlist,
    int TrackCount,
    int Volume,
    bool Muted,
    bool Shuffle,
    PlayerRepeat Repeat,
    OutputStatus Output,
    string? LastError)
{
    /// <summary>Whether sound is (or is about to be) coming out.</summary>
    public bool IsPlaying => State == PlayerState.Playing;

    /// <summary>The state as a word for the API: <c>stopped</c>, <c>playing</c>, <c>paused</c> or <c>waiting</c>.</summary>
    public string StateText => State switch
    {
        PlayerState.Playing => "playing",
        PlayerState.Paused => "paused",
        PlayerState.Waiting => "waiting",
        _ => "stopped",
    };
}

/// <summary>What the local API, the media keys and the quick panel can ask of the player.</summary>
public interface IPlayerControl
{
    /// <summary>Starts the next track (random when shuffle is on); does nothing when it is already playing.</summary>
    PlayerResult Play();

    /// <summary>Fades out (per the setting) and pauses.</summary>
    PlayerResult Pause();

    /// <summary>Fades out (per the setting) and stops.</summary>
    PlayerResult Stop();

    /// <summary>The next track.</summary>
    PlayerResult Next();

    /// <summary>Restarts the track when it is more than 3 seconds in, else goes back one track.</summary>
    PlayerResult Previous();

    /// <summary>The state now.</summary>
    PlayerStatus GetStatus();
}

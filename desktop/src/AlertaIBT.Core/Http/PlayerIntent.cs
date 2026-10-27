using System.Globalization;
using AlertaIBT.Core.Player;

namespace AlertaIBT.Core.Http;

/// <summary>What a request under <c>/player</c> of the local control API asks.</summary>
public enum PlayerIntentKind
{
    /// <summary>Start the next track (<c>GET|POST /player/play</c>).</summary>
    Play,

    /// <summary>Fade out and pause (<c>GET|POST /player/pause</c>): what Holyrics calls when its countdown ends.</summary>
    Pause,

    /// <summary>Fade out and stop (<c>GET|POST /player/stop</c>).</summary>
    Stop,

    /// <summary>The next track (<c>GET|POST /player/next</c>).</summary>
    Next,

    /// <summary>The previous track (<c>GET|POST /player/previous</c>).</summary>
    Previous,

    /// <summary>Report the state (<c>GET|POST /player/status</c>).</summary>
    Status,

    /// <summary>Under <c>/player</c> but not a route: answer 404.</summary>
    NotFound,
}

/// <summary>Reads a request of the local API under <c>/player</c>. Exact routes only (trailing slash and query string do not matter, case does not matter).</summary>
public static class PlayerIntent
{
    /// <summary>Whether the request target is <c>/player</c> or below it (the player owns the whole prefix, so a typo is a 404 and never an overlay action).</summary>
    public static bool IsPlayerPath(string rawUrl)
    {
        var path = PathOf(rawUrl);
        return path == "/player" || path.StartsWith("/player/", StringComparison.Ordinal);
    }

    /// <summary>The intent of a <c>GET</c> or <c>POST</c> under <c>/player</c>; anything else is <see cref="PlayerIntentKind.NotFound"/>.</summary>
    public static PlayerIntentKind Parse(string method, string rawUrl)
    {
        var verb = string.IsNullOrEmpty(method) ? "GET" : method.ToUpperInvariant();
        if (verb is not ("GET" or "POST"))
        {
            return PlayerIntentKind.NotFound;
        }

        return PathOf(rawUrl) switch
        {
            "/player/play" => PlayerIntentKind.Play,
            "/player/pause" => PlayerIntentKind.Pause,
            "/player/stop" => PlayerIntentKind.Stop,
            "/player/next" => PlayerIntentKind.Next,
            "/player/previous" => PlayerIntentKind.Previous,
            "/player/status" => PlayerIntentKind.Status,
            _ => PlayerIntentKind.NotFound,
        };
    }

    /// <summary>Does what the request asks and builds the answer body: <c>success</c> plus the whole status, or <c>success:false</c> with an <c>error</c>.</summary>
    /// <param name="intent">What was asked (not <see cref="PlayerIntentKind.NotFound"/>).</param>
    /// <param name="player">The player.</param>
    /// <param name="nextAlarm">The "next alarm" line, or <c>null</c>.</param>
    public static Dictionary<string, object?> Execute(PlayerIntentKind intent, IPlayerControl player, Func<string?>? nextAlarm = null)
    {
        ArgumentNullException.ThrowIfNull(player);

        var result = intent switch
        {
            PlayerIntentKind.Play => player.Play(),
            PlayerIntentKind.Pause => player.Pause(),
            PlayerIntentKind.Stop => player.Stop(),
            PlayerIntentKind.Next => player.Next(),
            PlayerIntentKind.Previous => player.Previous(),
            _ => PlayerResult.Ok,
        };

        var body = Describe(player.GetStatus(), nextAlarm?.Invoke());
        body["success"] = result.Success;
        if (!result.Success)
        {
            body["error"] = result.Error;
        }

        return body;
    }

    /// <summary>The status as the API shows it.</summary>
    public static Dictionary<string, object?> Describe(PlayerStatus status, string? nextAlarm)
    {
        ArgumentNullException.ThrowIfNull(status);

        return new Dictionary<string, object?>
        {
            ["success"] = true,
            ["enabled"] = status.Enabled,
            ["state"] = status.StateText,
            ["fading"] = status.Fading,
            ["track"] = status.Track is { } track
                ? new Dictionary<string, object?> { ["title"] = track.Title, ["artist"] = track.Artist, ["file"] = track.FileName }
                : null,
            ["positionSeconds"] = Seconds(status.Position),
            ["durationSeconds"] = Seconds(status.Duration),
            ["playlist"] = status.Playlist,
            ["trackCount"] = status.TrackCount,
            ["volume"] = status.Volume,
            ["muted"] = status.Muted,
            ["shuffle"] = status.Shuffle,
            ["repeat"] = PlayerRepeatText.ToText(status.Repeat),
            ["output"] = new Dictionary<string, object?>
            {
                ["text"] = status.Output.Text,
                ["device"] = status.Output.DeviceName,
                ["kind"] = status.Output.Kind.ToString(),
            },
            ["nextAlarm"] = nextAlarm,
            ["lastError"] = status.LastError,
        };
    }

    private static double Seconds(TimeSpan span) => double.Parse(span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static string PathOf(string rawUrl)
    {
        var path = (string.IsNullOrEmpty(rawUrl) ? "/" : rawUrl).Split('?')[0].TrimEnd('/').ToLowerInvariant();
        return path.Length == 0 ? "/" : path;
    }
}

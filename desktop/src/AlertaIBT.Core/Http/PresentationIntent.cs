using AlertaIBT.Core.Holyrics;

namespace AlertaIBT.Core.Http;

/// <summary>What a request under <c>/holyrics/presentation</c> of the local control API asks.</summary>
public enum PresentationIntentKind
{
    /// <summary>Mode ON (<c>GET|POST /holyrics/presentation/on</c>).</summary>
    On,

    /// <summary>Mode OFF (<c>GET|POST /holyrics/presentation/off</c>).</summary>
    Off,

    /// <summary>Report the state (<c>GET|POST /holyrics/presentation/status</c>).</summary>
    Status,

    /// <summary>Under <c>/holyrics</c> but not a route: answer 404.</summary>
    NotFound,
}

/// <summary>Reads a request of the local API under <c>/holyrics</c>, like <see cref="NdiIntent"/>: exact routes, any case, trailing slash and query ignored.</summary>
public static class PresentationIntent
{
    /// <summary>Whether the target is <c>/holyrics</c> or below it.</summary>
    public static bool IsHolyricsPath(string rawUrl)
    {
        var path = PathOf(rawUrl);
        return path == "/holyrics" || path.StartsWith("/holyrics/", StringComparison.Ordinal);
    }

    /// <summary>The intent of a <c>GET</c> or <c>POST</c> under <c>/holyrics</c>.</summary>
    public static PresentationIntentKind Parse(string method, string rawUrl)
    {
        var verb = string.IsNullOrEmpty(method) ? "GET" : method.ToUpperInvariant();
        if (verb is not ("GET" or "POST"))
        {
            return PresentationIntentKind.NotFound;
        }

        return PathOf(rawUrl) switch
        {
            "/holyrics/presentation/on" => PresentationIntentKind.On,
            "/holyrics/presentation/off" => PresentationIntentKind.Off,
            "/holyrics/presentation/status" => PresentationIntentKind.Status,
            _ => PresentationIntentKind.NotFound,
        };
    }

    /// <summary>Does what the request asks and builds the answer: <c>success</c>, <c>presentationOn</c> and, when it did not work, <c>error</c>.</summary>
    public static Dictionary<string, object?> Execute(PresentationIntentKind intent, IPresentationControl control)
    {
        ArgumentNullException.ThrowIfNull(control);

        var result = intent switch
        {
            PresentationIntentKind.On => control.SetOn(true),
            PresentationIntentKind.Off => control.SetOn(false),
            _ => PresentationResult.Ok,
        };

        var body = new Dictionary<string, object?> { ["success"] = result.Success, ["presentationOn"] = control.IsOn };
        if (result.Message is not null)
        {
            body[result.Success ? "message" : "error"] = result.Message;
        }

        return body;
    }

    private static string PathOf(string rawUrl)
    {
        var path = (string.IsNullOrEmpty(rawUrl) ? "/" : rawUrl).Split('?')[0].TrimEnd('/').ToLowerInvariant();
        return path.Length == 0 ? "/" : path;
    }
}

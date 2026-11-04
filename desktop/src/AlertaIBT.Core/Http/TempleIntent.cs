using AlertaIBT.Core.Temple;

namespace AlertaIBT.Core.Http;

/// <summary>What a request under <c>/templo</c> of the local control API asks.</summary>
public enum TempleIntentKind
{
    /// <summary>Mode ON (<c>GET|POST /templo/on</c>).</summary>
    On,

    /// <summary>Mode OFF (<c>GET|POST /templo/off</c>).</summary>
    Off,

    /// <summary>Report the mode, the list and the screen size (<c>GET|POST /templo/status</c>).</summary>
    Status,

    /// <summary>Under <c>/templo</c> but not a route: answer 404.</summary>
    NotFound,
}

/// <summary>Reads a request of the local API under <c>/templo</c>, like <see cref="PresentationIntent"/>: exact routes, any case, trailing slash and query ignored.</summary>
public static class TempleIntent
{
    /// <summary>Whether the target is <c>/templo</c> or below it.</summary>
    public static bool IsTemplePath(string rawUrl)
    {
        var path = PathOf(rawUrl);
        return path == "/templo" || path.StartsWith("/templo/", StringComparison.Ordinal);
    }

    /// <summary>The intent of a <c>GET</c> or <c>POST</c> under <c>/templo</c>.</summary>
    public static TempleIntentKind Parse(string method, string rawUrl)
    {
        var verb = string.IsNullOrEmpty(method) ? "GET" : method.ToUpperInvariant();
        if (verb is not ("GET" or "POST"))
        {
            return TempleIntentKind.NotFound;
        }

        return PathOf(rawUrl) switch
        {
            "/templo/on" => TempleIntentKind.On,
            "/templo/off" => TempleIntentKind.Off,
            "/templo/status" => TempleIntentKind.Status,
            _ => TempleIntentKind.NotFound,
        };
    }

    /// <summary>Does what the request asks (the settings are saved; the services follow) and builds the answer.</summary>
    public static Dictionary<string, object?> Execute(TempleIntentKind intent, TempleModeService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        switch (intent)
        {
            case TempleIntentKind.On:
                service.SetOn(true);
                break;
            case TempleIntentKind.Off:
                service.SetOn(false);
                break;
        }

        return service.StatusBody();
    }

    private static string PathOf(string rawUrl)
    {
        var path = (string.IsNullOrEmpty(rawUrl) ? "/" : rawUrl).Split('?')[0].TrimEnd('/').ToLowerInvariant();
        return path.Length == 0 ? "/" : path;
    }
}

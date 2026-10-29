using AlertaIBT.Core.Ndi;

namespace AlertaIBT.Core.Http;

/// <summary>What a request under <c>/ndi</c> of the local control API asks.</summary>
public enum NdiIntentKind
{
    /// <summary>Layer ON (<c>GET|POST /ndi/on</c>).</summary>
    On,

    /// <summary>Layer OFF (<c>GET|POST /ndi/off</c>).</summary>
    Off,

    /// <summary>Cut Preview to Program (<c>GET|POST /ndi/take</c>); it never selects cameras.</summary>
    Take,

    /// <summary>Report the state (<c>GET|POST /ndi/status</c>).</summary>
    Status,

    /// <summary>Under <c>/ndi</c> but not a route: answer 404.</summary>
    NotFound,
}

/// <summary>Reads a request of the local API under <c>/ndi</c>, like <see cref="PlayerIntent"/>: exact routes, any case, trailing slash and query ignored.</summary>
public static class NdiIntent
{
    /// <summary>Whether the target is <c>/ndi</c> or below it.</summary>
    public static bool IsNdiPath(string rawUrl)
    {
        var path = PathOf(rawUrl);
        return path == "/ndi" || path.StartsWith("/ndi/", StringComparison.Ordinal);
    }

    /// <summary>The intent of a <c>GET</c> or <c>POST</c> under <c>/ndi</c>.</summary>
    public static NdiIntentKind Parse(string method, string rawUrl)
    {
        var verb = string.IsNullOrEmpty(method) ? "GET" : method.ToUpperInvariant();
        if (verb is not ("GET" or "POST"))
        {
            return NdiIntentKind.NotFound;
        }

        return PathOf(rawUrl) switch
        {
            "/ndi/on" => NdiIntentKind.On,
            "/ndi/off" => NdiIntentKind.Off,
            "/ndi/take" => NdiIntentKind.Take,
            "/ndi/status" => NdiIntentKind.Status,
            _ => NdiIntentKind.NotFound,
        };
    }

    /// <summary>Does what the request asks and builds the answer: <c>success</c> plus the status (and <c>error</c> / <c>message</c> when relevant).</summary>
    public static Dictionary<string, object?> Execute(NdiIntentKind intent, INdiControl ndi)
    {
        ArgumentNullException.ThrowIfNull(ndi);

        var result = intent switch
        {
            NdiIntentKind.On => ndi.SetLayer(true),
            NdiIntentKind.Off => ndi.SetLayer(false),
            NdiIntentKind.Take => ndi.Take(),
            _ => NdiResult.Ok,
        };

        var body = Describe(ndi.GetStatus());
        body["success"] = result.Success;
        if (result.Message is not null)
        {
            body[result.Success ? "message" : "error"] = result.Message;
        }

        return body;
    }

    /// <summary>The status as the API shows it (slots are 1-based here, as the owner reads them).</summary>
    public static Dictionary<string, object?> Describe(NdiStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        var s = status.Switcher;
        return new Dictionary<string, object?>
        {
            ["success"] = true,
            ["runtimeOk"] = status.RuntimeOk,
            ["runtime"] = status.RuntimeText,
            ["layerOn"] = s.LayerOn,
            ["windowVisible"] = s.WindowVisible,
            ["testPattern"] = s.TestPattern,
            ["preview"] = Camera(s.PreviewSlot, s.PreviewName, s.PreviewState),
            ["program"] = Camera(s.ProgramSlot, s.ProgramName, s.ProgramState),
            ["takePending"] = s.TakePending,
            ["fps"] = s.ProgramFps,
            ["width"] = s.ProgramWidth,
            ["height"] = s.ProgramHeight,
            ["lastFrameAgeMs"] = s.ProgramLastFrameAgeMs,
            ["framesDropped"] = s.ProgramFramesDropped,
            ["lastError"] = s.LastError,
        };
    }

    private static Dictionary<string, object?> Camera(int? slot, string? name, NdiConnectionState state) =>
        new() { ["slot"] = slot is int s ? s + 1 : null, ["name"] = name, ["state"] = state.ToString() };

    private static string PathOf(string rawUrl)
    {
        var path = (string.IsNullOrEmpty(rawUrl) ? "/" : rawUrl).Split('?')[0].TrimEnd('/').ToLowerInvariant();
        return path.Length == 0 ? "/" : path;
    }
}

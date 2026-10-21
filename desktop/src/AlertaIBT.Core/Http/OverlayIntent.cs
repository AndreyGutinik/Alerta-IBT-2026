using System.Text.RegularExpressions;

namespace AlertaIBT.Core.Http;

/// <summary>What a request to the local control API (<c>127.0.0.1:8787</c>) asks of the overlay.</summary>
public enum OverlayIntentKind
{
    /// <summary>Report whether the overlay is enabled (<c>GET /overlay/status</c>).</summary>
    Status,

    /// <summary>Turn the overlay on: resume after the sermon.</summary>
    On,

    /// <summary>Turn the overlay off: the sermon began, so cancel everything and refuse new alerts.</summary>
    Off,

    /// <summary>Not a route of the API: answer 404.</summary>
    NotFound,
}

/// <summary>
/// Reads a request of the local control API the way the old Electron app did (<c>startHttpControlServer</c> and
/// <c>detectOverlayIntent</c> in <c>src/main.js</c> in the tag v7.0.0), so the Holyrics trigger keeps working untouched.
/// </summary>
/// <remarks>
/// <c>OPTIONS</c> (the CORS preflight, answered 204 for any path) is not a route and gives
/// <see cref="OverlayIntentKind.NotFound"/> here: the HTTP layer answers it before asking for the intent.
/// </remarks>
public static partial class OverlayIntent
{
    private const string Prefix = "/overlay";

    /// <summary>
    /// The intent of a request. The exact routes are <c>GET|POST /overlay/off</c>, <c>GET|POST /overlay/on</c> and
    /// <c>GET /overlay/status</c> (trailing slashes and the query string do not matter). Any other <c>GET|POST</c> under
    /// <c>/overlay</c> falls back to the words in the whole URL: <c>off</c> as a path or query item, <c>pause</c> or
    /// <c>disable</c> mean off; <c>on</c>, <c>resume</c> or <c>enable</c> mean on; off wins when both appear.
    /// </summary>
    /// <param name="method">The HTTP method, in any case; null or empty counts as <c>GET</c>.</param>
    /// <param name="rawUrl">The request target: path and query, as received; null or empty counts as <c>/</c>.</param>
    public static OverlayIntentKind Parse(string method, string rawUrl)
    {
        var verb = string.IsNullOrEmpty(method) ? "GET" : method.ToUpperInvariant();
        var url = string.IsNullOrEmpty(rawUrl) ? "/" : rawUrl;
        var isGet = verb == "GET";
        var isGetOrPost = isGet || verb == "POST";

        var path = url.Split('?')[0].TrimEnd('/');
        if (path.Length == 0)
        {
            path = "/";
        }

        if (isGetOrPost && path == "/overlay/off")
        {
            return OverlayIntentKind.Off;
        }

        if (isGetOrPost && path == "/overlay/on")
        {
            return OverlayIntentKind.On;
        }

        if (isGet && path == "/overlay/status")
        {
            return OverlayIntentKind.Status;
        }

        if (isGetOrPost && path.StartsWith(Prefix, StringComparison.Ordinal))
        {
            var lowered = url.ToLowerInvariant();
            if (OffWord().IsMatch(lowered)
                || lowered.Contains("pause", StringComparison.Ordinal)
                || lowered.Contains("disable", StringComparison.Ordinal))
            {
                return OverlayIntentKind.Off;
            }

            if (OnWord().IsMatch(lowered)
                || lowered.Contains("resume", StringComparison.Ordinal)
                || lowered.Contains("enable", StringComparison.Ordinal))
            {
                return OverlayIntentKind.On;
            }
        }

        return OverlayIntentKind.NotFound;
    }

    [GeneratedRegex(@"(^|[/?&=])off([/?&=]|\z)")]
    private static partial Regex OffWord();

    [GeneratedRegex(@"(^|[/?&=])on([/?&=]|\z)")]
    private static partial Regex OnWord();
}

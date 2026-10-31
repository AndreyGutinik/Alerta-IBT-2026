using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AlertaIBT.Core.Tests.Holyrics;

/// <summary>One request the fake Holyrics received.</summary>
internal sealed record HolyricsRequest(string Method, string Action, string Token, string Body, string? ContentType);

/// <summary>What the fake answers to one action.</summary>
internal sealed record HolyricsReply(int Status, string Json, int DelayMs = 0)
{
    public static HolyricsReply Ok { get; } = new(200, "{\"status\":\"ok\"}");

    public static HolyricsReply WithData(string data) => new(200, "{\"status\":\"ok\",\"data\":" + data + "}");

    public static HolyricsReply Error(string message) => new(200, "{\"status\":\"error\",\"error\":\"" + message + "\"}");

    public static HolyricsReply Unauthorized { get; } = new(401, "{\"status\": \"error\", \"error\": \"unauthorized action\"}");
}

/// <summary>A tiny stand-in for the HTTP API server of Holyrics (an <see cref="HttpListener"/> on a free local port) that records what it is asked.</summary>
internal sealed class FakeHolyricsServer : IDisposable
{
    public const string GoodToken = "tk-TESTONLY-0123456789";

    private readonly HttpListener _listener = new();
    private readonly List<HolyricsRequest> _requests = [];
    private readonly object _gate = new();
    private readonly Task _loop;

    public FakeHolyricsServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _loop = Task.Run(Loop);
    }

    public int Port { get; }

    /// <summary>The token the fake accepts; any other token gets the 401 answer.</summary>
    public string AcceptedToken { get; set; } = GoodToken;

    /// <summary>Whether a media player is "playing" (read by GetMediaPlayerInfo, flipped by MediaPlayerAction).</summary>
    public bool Playing { get; set; }

    /// <summary>Answers per action name; an action without one gets <see cref="HolyricsReply.Ok"/>.</summary>
    public Dictionary<string, HolyricsReply> Replies { get; } = [];

    public IReadOnlyList<HolyricsRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    public IReadOnlyList<string> Actions => Requests.Select(r => r.Action).ToList();

    public void Dispose()
    {
        try
        {
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
            // already closed
        }

        try
        {
            _loop.Wait(2000);
        }
        catch (AggregateException)
        {
            // the loop ends with the listener
        }
    }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => Handle(context));
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);
            var path = context.Request.Url!.AbsolutePath;
            var action = path.StartsWith("/api/", StringComparison.Ordinal) ? path["/api/".Length..] : path;
            var token = context.Request.QueryString["token"] ?? string.Empty;
            lock (_gate)
            {
                _requests.Add(new HolyricsRequest(context.Request.HttpMethod, action, token, body, context.Request.ContentType));
            }

            HolyricsReply reply;
            if (!string.Equals(context.Request.ContentType, "application/json", StringComparison.Ordinal))
            {
                reply = new HolyricsReply(415, "{}"); // the real Holyrics refuses a charset (or any other type) here, even with a wrong token
            }
            else if (token != AcceptedToken)
            {
                reply = HolyricsReply.Unauthorized;
            }
            else if (Replies.TryGetValue(action, out var custom))
            {
                reply = custom;
            }
            else if (action == "GetMediaPlayerInfo")
            {
                reply = HolyricsReply.WithData("{\"name\":\"v.mp4\",\"playing\":" + (Playing ? "true" : "false") + ",\"time_ms\":-1}");
            }
            else
            {
                if (action == "MediaPlayerAction")
                {
                    Playing = body.Contains("\"play\"", StringComparison.Ordinal);
                }

                reply = HolyricsReply.Ok;
            }

            if (reply.DelayMs > 0)
            {
                await Task.Delay(reply.DelayMs).ConfigureAwait(false);
            }

            var bytes = Encoding.UTF8.GetBytes(reply.Json);
            context.Response.StatusCode = reply.Status;
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.Close();
        }
        catch (Exception)
        {
            // the client gave up (a timeout test): nothing to answer
        }
    }
}

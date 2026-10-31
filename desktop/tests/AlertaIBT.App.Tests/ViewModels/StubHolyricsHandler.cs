using System.Net;
using System.Net.Http;
using System.Text;

namespace AlertaIBT.App.Tests.ViewModels;

/// <summary>An HTTP handler that stands in for the API server of Holyrics: it answers from a function and remembers what it was asked.</summary>
internal sealed class StubHolyricsHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<string> _actions = [];

    /// <summary>The answer for an action name; the default is <c>{"status":"ok"}</c>.</summary>
    public Func<string, (HttpStatusCode Status, string Json)> Reply { get; set; } = _ => (HttpStatusCode.OK, "{\"status\":\"ok\"}");

    /// <summary>When set, every request fails as if nobody answered.</summary>
    public bool Down { get; set; }

    public IReadOnlyList<string> Actions
    {
        get
        {
            lock (_gate)
            {
                return _actions.ToList();
            }
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var action = request.RequestUri!.AbsolutePath.Replace("/api/", string.Empty, StringComparison.Ordinal);
        lock (_gate)
        {
            _actions.Add(action);
        }

        if (Down)
        {
            throw new HttpRequestException("down");
        }

        var (status, json) = Reply(action);
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}

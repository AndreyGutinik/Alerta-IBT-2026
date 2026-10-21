using AlertaIBT.Core.Correction;

namespace AlertaIBT.Core.Tests.Correction;

// One long-lived connection pool keeps the TLS connection to Gemini warm between messages, with no extra API calls.
public class GeminiHttpTests
{
    [Fact]
    public void The_handler_recycles_connections_every_five_minutes_and_drops_idle_ones_after_two()
    {
        using var handler = GeminiHttp.CreateHandler();

        Assert.Equal(TimeSpan.FromMinutes(5), handler.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromMinutes(2), handler.PooledConnectionIdleTimeout);
    }

    [Fact]
    public void The_client_has_the_request_timeout_of_ten_seconds()
    {
        using var client = GeminiHttp.CreateClient();

        Assert.Equal(TimeSpan.FromSeconds(10), client.Timeout);
    }
}

using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;

namespace AlertaIBT.Core.Tests.Services;

// The site gives its alerts the id of the request, so every later event of the queue can be matched to the request.
public sealed class AlertServiceItemIdTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static ServiceHarness Quiet()
    {
        var h = new ServiceHarness(start: false);
        h.Display.SetLed(false);
        h.Service.Start();
        return h;
    }

    private static Task<SubmitOutcome> Site(ServiceHarness h, string raw, string id) =>
        h.Service.SubmitAsync(raw, AlertSource.Site, SenderRole.Member, false, "u1", Ct, id);

    [Fact]
    public async Task A_queued_alert_has_the_given_id()
    {
        using var h = Quiet();

        var outcome = await Site(h, "corolla prata abc1d23", "req-1");

        Assert.Equal("req-1", outcome.Id);
        Assert.Equal("req-1", h.Queue.Pending[0].Id);
    }

    [Fact]
    public async Task A_refused_alert_keeps_the_id_in_the_history()
    {
        using var h = Quiet();

        await Site(h, "corolla prata", "req-missing");
        await Site(h, "vai tomar no cu seu idiota", "req-vulgar");
        await Site(h, "corolla prata abc1d23 " + new string('x', 400), "req-long");
        await Site(h, "corolla prata abc1d24", "req-ok");
        await Site(h, "corolla prata abc1d24", "req-duplicate");
        h.Service.Pause();
        await Site(h, "civic branco ijk1l22", "req-paused");

        var ids = h.Queue.History.Select(e => e.Item.Id).ToArray();
        foreach (var id in new[] { "req-missing", "req-vulgar", "req-long", "req-duplicate", "req-paused", "req-ok" })
        {
            Assert.Contains(id, ids);
        }
    }

    [Fact]
    public async Task Without_an_id_a_new_one_is_made()
    {
        using var h = Quiet();

        var outcome = await h.Telegram("corolla prata abc1d23");

        Assert.StartsWith("id", outcome.Id, StringComparison.Ordinal);
    }
}

using AlertaIBT.Core.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Diagnostics;

// Batch 42: the same breadcrumb twice in a row is one entry with a count (a key tapped ten times must not push everything else out of a 24-entry trail).
public sealed class BreadcrumbCoalescingTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 9, 14, 0, 0, TimeSpan.Zero));

    [Fact]
    public void The_same_breadcrumb_twice_in_a_row_is_one_entry_with_a_count_and_the_time_of_the_last()
    {
        var trail = new BreadcrumbTrail(8, _clock);
        trail.Add("ndi: previa 1");
        _clock.Advance(TimeSpan.FromMilliseconds(150));
        trail.Add("ndi: previa 1");
        trail.Add("ndi: previa 1");
        trail.Add("ndi: Take");
        trail.Add("ndi: previa 1");

        var entries = trail.Snapshot();

        Assert.Equal(["ndi: previa 1 x3", "ndi: Take", "ndi: previa 1"], entries.Select(e => e.Text));
        Assert.Contains("ndi: previa 1 x3", trail.Format(_clock.GetUtcNow()), StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_tapped_forty_times_is_one_entry_and_does_not_push_the_other_entries_out_of_the_trail()
    {
        var single = new BreadcrumbTrail(4, _clock);
        single.Add("configuracao: Ndi.LayerOn");
        for (var i = 0; i < 40; i++)
        {
            single.Add("ndi: previa 1");
        }

        Assert.Equal(2, single.Snapshot().Count);
        Assert.Equal("configuracao: Ndi.LayerOn", single.Snapshot()[0].Text);
        Assert.Equal("ndi: previa 1 x40", single.Snapshot()[1].Text);
    }
}

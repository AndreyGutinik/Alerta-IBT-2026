using AlertaIBT.Core.Firebase;

namespace AlertaIBT.Core.Tests.Firebase;

public sealed class SseParserTests
{
    private static SseEvent[] FeedAll(SseParser parser, params string[] chunks) =>
        chunks.SelectMany(c => parser.Feed(c)).ToArray();

    [Fact]
    public void A_complete_event_is_returned()
    {
        var events = FeedAll(new SseParser(), "event: put\ndata: {\"path\":\"/\"}\n\n");

        var evt = Assert.Single(events);
        Assert.Equal("put", evt.Name);
        Assert.Equal("{\"path\":\"/\"}", evt.Data);
    }

    [Fact]
    public void Nothing_is_returned_before_the_blank_line()
    {
        var parser = new SseParser();

        Assert.Empty(parser.Feed("event: put\ndata: 1\n"));
        Assert.Single(parser.Feed("\n"));
    }

    [Fact]
    public void A_chunk_may_end_in_the_middle_of_a_field()
    {
        var events = FeedAll(new SseParser(), "eve", "nt: pa", "tch\nda", "ta: {\"a\":", "1}\n", "\n");

        var evt = Assert.Single(events);
        Assert.Equal("patch", evt.Name);
        Assert.Equal("{\"a\":1}", evt.Data);
    }

    [Fact]
    public void Crlf_line_ends_work()
    {
        var events = FeedAll(new SseParser(), "event: put\r\ndata: x\r\n\r\n");

        var evt = Assert.Single(events);
        Assert.Equal("put", evt.Name);
        Assert.Equal("x", evt.Data);
    }

    [Fact]
    public void A_crlf_pair_split_between_two_chunks_is_one_line_end()
    {
        // "data: x\r" | "\n" | "\r" | "\n": two line ends (a blank line), not four.
        var events = FeedAll(new SseParser(), "event: put\r\ndata: x\r", "\n", "\r", "\n");

        var evt = Assert.Single(events);
        Assert.Equal("x", evt.Data);
    }

    [Fact]
    public void A_lone_cr_ends_a_line()
    {
        var events = FeedAll(new SseParser(), "event: put\rdata: y\r\r");

        var evt = Assert.Single(events);
        Assert.Equal("y", evt.Data);
    }

    [Fact]
    public void Several_data_lines_are_joined_with_a_line_feed()
    {
        var events = FeedAll(new SseParser(), "event: put\ndata: one\ndata: two\ndata:\ndata: four\n\n");

        Assert.Equal("one\ntwo\n\nfour", Assert.Single(events).Data);
    }

    [Fact]
    public void Comments_and_unknown_fields_are_ignored()
    {
        var events = FeedAll(new SseParser(), ": a comment\nid: 7\nretry: 100\nevent: put\nbogus: 1\ndata: z\n\n");

        var evt = Assert.Single(events);
        Assert.Equal("put", evt.Name);
        Assert.Equal("z", evt.Data);
    }

    [Fact]
    public void An_event_without_a_name_is_a_message()
    {
        var evt = Assert.Single(FeedAll(new SseParser(), "data: hello\n\n"));

        Assert.Equal("message", evt.Name);
    }

    [Fact]
    public void Only_the_first_space_after_the_colon_is_dropped()
    {
        var evt = Assert.Single(FeedAll(new SseParser(), "event:put\ndata:  two spaces\n\n"));

        Assert.Equal("put", evt.Name);
        Assert.Equal(" two spaces", evt.Data);
    }

    [Fact]
    public void Blank_lines_between_events_do_not_make_empty_events()
    {
        var events = FeedAll(new SseParser(), "\n\n\nevent: keep-alive\ndata: null\n\n\n\nevent: put\ndata: 1\n\n");

        Assert.Equal(["keep-alive", "put"], events.Select(e => e.Name));
    }

    [Fact]
    public void Several_events_in_one_chunk_come_out_in_order()
    {
        var events = FeedAll(new SseParser(), "event: a\ndata: 1\n\nevent: b\ndata: 2\n\nevent: c\ndata: 3\n\n");

        Assert.Equal(["1", "2", "3"], events.Select(e => e.Data));
    }

    [Fact]
    public void Feeding_one_character_at_a_time_gives_the_same_events()
    {
        const string text = "event: put\r\ndata: {\"path\":\"/\",\"data\":{\"a\":1}}\r\n\r\n: ka\r\nevent: keep-alive\r\ndata: null\r\n\r\n";
        var whole = FeedAll(new SseParser(), text);
        var parser = new SseParser();
        var bits = text.SelectMany(c => parser.Feed(c.ToString())).ToArray();

        Assert.Equal(whole, bits);
        Assert.Equal(2, bits.Length);
    }
}

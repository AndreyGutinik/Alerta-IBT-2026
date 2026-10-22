using AlertaIBT.Core.Services;
using AlertaIBT.Core.Telegram;

namespace AlertaIBT.Core.Tests.Services;

// The journal learns of every reply the bot sends and every # message the engine takes, so "Limpar historico" can delete them.
public sealed class TelegramJournalingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-journaling-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTelegramApi _api = new();
    private TelegramEngine? _engine;
    private ServiceHarness? _h;

    public TelegramJournalingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _engine?.Dispose();
        _h?.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // The OS cleans the temp folder.
        }
    }

    private TelegramJournal NewJournal(TimeProvider? clock = null) => new(Path.Combine(_dir, "telegram-journal.json"), clock);

    [Fact]
    public async Task A_reply_sent_to_the_group_is_noted_with_the_id_telegram_gave()
    {
        var journal = NewJournal();
        using var notifier = new TelegramGroupNotifier(_api.Client(), () => FakeTelegramApi.Chat, new CapturingLogger(), (_, _) => Task.CompletedTask, journal);

        notifier.Notify("oi");

        await Wait.Until(() => journal.Snapshot().Count == 1, "reply journaled");
        var entry = journal.Snapshot()[0];
        Assert.Equal(FakeTelegramApi.Chat, entry.ChatId);
        Assert.Equal(1, entry.MessageId); // the fake answers message_id 1
    }

    [Fact]
    public async Task A_reply_that_failed_is_not_noted()
    {
        _api.FailNextSends(2);
        var journal = NewJournal();
        using var notifier = new TelegramGroupNotifier(_api.Client(), () => FakeTelegramApi.Chat, new CapturingLogger(), (_, _) => Task.CompletedTask, journal);

        notifier.Notify("lost");
        await Wait.Until(() => _api.SendAttempts == 2, "two attempts");
        await Wait.Settle();

        Assert.Empty(journal.Snapshot());
    }

    [Fact]
    public async Task Accepted_refused_and_too_old_hash_messages_are_all_noted_by_the_engine()
    {
        _h = new ServiceHarness();
        var journal = NewJournal(_h.Clock);
        var poller = new TelegramPoller(_api.Client(), () => FakeTelegramApi.Chat, _ => { }, 0, (_, ct) => Task.Delay(1, ct));
        _engine = new TelegramEngine(poller, _h.Service, _h.Notifier, _h.Hub, _h.Logger, _h.Clock, journal);
        var now = _h.Clock.GetUtcNow();
        _api.Updates(
            FakeTelegramApi.Message(10, "#corolla prata abc1d23 vidro aberto", now.ToUnixTimeSeconds())
            + "," + FakeTelegramApi.Message(11, "#gol branco farol aceso", now.ToUnixTimeSeconds())
            + "," + FakeTelegramApi.Message(12, "#onix preto bbb2222", now.AddHours(-2).ToUnixTimeSeconds())
            + "," + FakeTelegramApi.Message(13, "conversa sem hashtag", now.ToUnixTimeSeconds()));

        _engine.Start();

        await Wait.Until(() => journal.Snapshot().Count == 3, "three alert messages journaled");
        Assert.Equal([10L, 11L, 12L], journal.Snapshot().Select(e => e.MessageId).Order());
        Assert.All(journal.Snapshot(), e => Assert.Equal(FakeTelegramApi.Chat, e.ChatId));

        // The journal has the time the message was sent, not the time it was handled.
        Assert.Equal(now.AddHours(-2).ToUnixTimeSeconds(), journal.Snapshot().Single(e => e.MessageId == 12).SentAt.ToUnixTimeSeconds());
    }
}

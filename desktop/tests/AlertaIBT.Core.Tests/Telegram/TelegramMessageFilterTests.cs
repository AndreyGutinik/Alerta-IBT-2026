using System.Text.Json;
using AlertaIBT.Core.Telegram;

namespace AlertaIBT.Core.Tests.Telegram;

public class TelegramMessageFilterTests
{
    private const long GroupId = -1001234567890;

    private static TgMessage Message(
        string? text,
        long chatId = GroupId,
        TgUser? from = null,
        TgChat? senderChat = null)
    {
        return new TgMessage(
            MessageId: 1,
            From: from ?? new TgUser(42, false, "Maria", "maria"),
            SenderChat: senderChat,
            Chat: new TgChat(chatId, "supergroup", "Estacionamento"),
            Date: 1760000000,
            Text: text);
    }

    [Fact]
    public void TryAccept_RightChatWithHash_ReturnsTheTextWithoutIt()
    {
        Assert.Equal("gol branco", TelegramMessageFilter.TryAccept(Message("#gol branco"), GroupId));
    }

    [Theory]
    [InlineData("  ## gol branco ", "gol branco")]
    [InlineData("#gol branco\nabc1d23", "gol branco\nabc1d23")]
    [InlineData("#  gol branco", "gol branco")]
    [InlineData("#", "")]
    [InlineData("  #  ", "")]
    public void TryAccept_RemovesEveryLeadingHashAndTrims(string text, string expected)
    {
        Assert.Equal(expected, TelegramMessageFilter.TryAccept(Message(text), GroupId));
    }

    [Fact]
    public void TryAccept_KeepsAHashInTheMiddle()
    {
        Assert.Equal("gol #branco", TelegramMessageFilter.TryAccept(Message("#gol #branco"), GroupId));
    }

    [Theory]
    [InlineData("gol branco")]
    [InlineData("gol branco #abc")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryAccept_WithoutLeadingHash_ReturnsNull(string? text)
    {
        Assert.Null(TelegramMessageFilter.TryAccept(Message(text), GroupId));
    }

    [Fact]
    public void TryAccept_OtherChat_ReturnsNull()
    {
        Assert.Null(TelegramMessageFilter.TryAccept(Message("#gol branco", chatId: -999), GroupId));
    }

    [Fact]
    public void TryAccept_ChatNotConfigured_ReturnsNull()
    {
        Assert.Null(TelegramMessageFilter.TryAccept(Message("#gol branco", chatId: 0), 0));
        Assert.Null(TelegramMessageFilter.TryAccept(Message("#gol branco"), 0));
    }

    [Fact]
    public void TryAccept_Bot_ReturnsNull()
    {
        var bot = new TgUser(7, true, "Bot", "some_bot");

        Assert.Null(TelegramMessageFilter.TryAccept(Message("#gol branco", from: bot), GroupId));
    }

    [Fact]
    public void TryAccept_AnonymousAdminOfTheSameGroup_IsAccepted()
    {
        var groupAnonymousBot = new TgUser(1087968824, true, "Group", "GroupAnonymousBot");
        var sameGroup = new TgChat(GroupId, "supergroup", "Estacionamento");

        Assert.Equal(
            "gol branco",
            TelegramMessageFilter.TryAccept(Message("#gol branco", from: groupAnonymousBot, senderChat: sameGroup), GroupId));
    }

    [Fact]
    public void TryAccept_ChannelPostForwardedIntoTheGroup_ReturnsNull()
    {
        var telegram = new TgUser(777000, true, "Telegram", null);
        var channel = new TgChat(-1009876543210, "channel", "Canal");

        Assert.Null(TelegramMessageFilter.TryAccept(Message("#gol branco", from: telegram, senderChat: channel), GroupId));
    }

    [Fact]
    public void TryAccept_LinkedChannelAutoForward_NonBotSenderWithForeignSenderChat_ReturnsNull()
    {
        // Telegram auto-forwards linked-channel posts with from = user 777000, which is not flagged as a bot.
        var telegram = new TgUser(777000, false, "Telegram", null);
        var channel = new TgChat(-1009876543210, "channel", "Canal");

        Assert.Null(TelegramMessageFilter.TryAccept(Message("#gol branco", from: telegram, senderChat: channel), GroupId));
    }

    [Fact]
    public void TryAccept_ForeignSenderChatWithoutSender_ReturnsNull()
    {
        var channel = new TgChat(-1009876543210, "channel", "Canal");
        var message = Message("#gol branco", senderChat: channel) with { From = null };

        Assert.Null(TelegramMessageFilter.TryAccept(message, GroupId));
    }

    [Fact]
    public void TryAccept_SameGroupSenderChatWithNonBotSender_IsAccepted()
    {
        var sameGroup = new TgChat(GroupId, "supergroup", "Estacionamento");

        Assert.Equal("gol branco", TelegramMessageFilter.TryAccept(Message("#gol branco", senderChat: sameGroup), GroupId));
    }

    [Fact]
    public void TryAccept_MessageWithoutSender_IsAccepted()
    {
        var message = Message("#gol branco") with { From = null };

        Assert.Equal("gol branco", TelegramMessageFilter.TryAccept(message, GroupId));
    }

    [Fact]
    public void TryAccept_NullMessageThrows()
    {
        Assert.Throws<ArgumentNullException>(() => TelegramMessageFilter.TryAccept(null!, GroupId));
    }

    [Fact]
    public void GetUpdatesJson_DeserializesWithDefaultOptionsAndFeedsTheFilter()
    {
        const string json = """
            {
              "ok": true,
              "result": [
                {
                  "update_id": 100,
                  "message": {
                    "message_id": 11,
                    "from": { "id": 42, "is_bot": false, "first_name": "Maria", "username": "maria", "language_code": "pt-br" },
                    "chat": { "id": -1001234567890, "title": "Estacionamento", "type": "supergroup" },
                    "date": 1760000000,
                    "text": "#gol branco abc1d23 farol aceso",
                    "entities": [ { "offset": 0, "length": 4, "type": "hashtag" } ]
                  }
                },
                {
                  "update_id": 101,
                  "message": {
                    "message_id": 12,
                    "from": { "id": 1087968824, "is_bot": true, "first_name": "Group", "username": "GroupAnonymousBot" },
                    "sender_chat": { "id": -1001234567890, "title": "Estacionamento", "type": "supergroup" },
                    "chat": { "id": -1001234567890, "title": "Estacionamento", "type": "supergroup" },
                    "date": 1760000060,
                    "text": "#fusca azul"
                  }
                },
                {
                  "update_id": 102,
                  "edited_message": { "message_id": 13 }
                }
              ]
            }
            """;

        var response = JsonSerializer.Deserialize<TgResponse<List<TgUpdate>>>(json);

        Assert.NotNull(response);
        Assert.True(response.Ok);
        Assert.Null(response.Description);
        Assert.Null(response.ErrorCode);
        Assert.NotNull(response.Result);
        Assert.Equal(3, response.Result.Count);

        var first = response.Result[0];
        Assert.Equal(100, first.UpdateId);
        Assert.NotNull(first.Message);
        Assert.Equal(11, first.Message.MessageId);
        Assert.Equal(1760000000, first.Message.Date);
        Assert.Equal(-1001234567890, first.Message.Chat.Id);
        Assert.Equal("supergroup", first.Message.Chat.Type);
        Assert.Equal("Estacionamento", first.Message.Chat.Title);
        Assert.NotNull(first.Message.From);
        Assert.Equal(42, first.Message.From.Id);
        Assert.False(first.Message.From.IsBot);
        Assert.Equal("Maria", first.Message.From.FirstName);
        Assert.Equal("maria", first.Message.From.Username);
        Assert.Null(first.Message.SenderChat);
        Assert.Equal("gol branco abc1d23 farol aceso", TelegramMessageFilter.TryAccept(first.Message, GroupId));

        var second = response.Result[1];
        Assert.NotNull(second.Message);
        Assert.True(second.Message.From!.IsBot);
        Assert.Equal(GroupId, second.Message.SenderChat!.Id);
        Assert.Equal("fusca azul", TelegramMessageFilter.TryAccept(second.Message, GroupId));

        Assert.Null(response.Result[2].Message);
    }

    [Fact]
    public void ErrorJson_DeserializesDescriptionAndCode()
    {
        const string json = """{"ok":false,"error_code":409,"description":"Conflict: terminated by other getUpdates request"}""";

        var response = JsonSerializer.Deserialize<TgResponse<List<TgUpdate>>>(json);

        Assert.NotNull(response);
        Assert.False(response.Ok);
        Assert.Null(response.Result);
        Assert.Equal(409, response.ErrorCode);
        Assert.Equal("Conflict: terminated by other getUpdates request", response.Description);
    }
}

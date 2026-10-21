using System.Text.Json.Serialization;

namespace AlertaIBT.Core.Telegram;

/// <summary>The envelope of every Telegram Bot API response.</summary>
/// <param name="Ok">Whether the call worked.</param>
/// <param name="Result">The payload, when it worked.</param>
/// <param name="Description">What went wrong, when it did not.</param>
/// <param name="ErrorCode">The error code, when it did not work.</param>
public sealed record TgResponse<T>(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("result")] T? Result,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("error_code")] int? ErrorCode);

/// <summary>One item of <c>getUpdates</c>.</summary>
/// <param name="UpdateId">The update's id; the next poll asks for <c>UpdateId + 1</c>.</param>
/// <param name="Message">The new message, when the update is one (other kinds of update leave it null).</param>
public sealed record TgUpdate(
    [property: JsonPropertyName("update_id")] long UpdateId,
    [property: JsonPropertyName("message")] TgMessage? Message);

/// <summary>A Telegram message.</summary>
/// <param name="MessageId">The message's id inside its chat.</param>
/// <param name="From">Who sent it; null for posts sent on behalf of a channel.</param>
/// <param name="SenderChat">The chat on whose behalf it was sent: the group itself for an anonymous admin.</param>
/// <param name="Chat">The chat it was sent to.</param>
/// <param name="Date">When it was sent, in Unix seconds.</param>
/// <param name="Text">The text, for text messages.</param>
public sealed record TgMessage(
    [property: JsonPropertyName("message_id")] long MessageId,
    [property: JsonPropertyName("from")] TgUser? From,
    [property: JsonPropertyName("sender_chat")] TgChat? SenderChat,
    [property: JsonPropertyName("chat")] TgChat Chat,
    [property: JsonPropertyName("date")] long Date,
    [property: JsonPropertyName("text")] string? Text);

/// <summary>A Telegram chat.</summary>
/// <param name="Id">The chat's id (negative for groups and channels).</param>
/// <param name="Type"><c>private</c>, <c>group</c>, <c>supergroup</c> or <c>channel</c>.</param>
/// <param name="Title">The title of a group or channel.</param>
public sealed record TgChat(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("title")] string? Title);

/// <summary>A Telegram user or bot.</summary>
/// <param name="Id">The user's id.</param>
/// <param name="IsBot">Whether it is a bot.</param>
/// <param name="FirstName">The first name.</param>
/// <param name="Username">The username, without the <c>@</c>.</param>
public sealed record TgUser(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("is_bot")] bool IsBot,
    [property: JsonPropertyName("first_name")] string? FirstName,
    [property: JsonPropertyName("username")] string? Username);

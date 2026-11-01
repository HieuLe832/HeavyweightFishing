using Chat.Api.Auth;
using Chat.Api.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Chat.Api.Messaging;

/// <summary>Process 4.0 over WebSocket: the entry point for message commands. Failures are HubExceptions carrying the error code.</summary>
[Authorize]
public class ChatHub(
    CommandValidator validator, MessageWriter writer, MessageBroadcaster broadcaster,
    MessageRateLimiter limiter, IOptions<MessagingOptions> options,
    ReceiptService receipts, PresenceService presence, PresenceAnnouncer announcer, TypingRateLimiter typingLimiter,
    SessionGuard sessions) : Hub
{
    public override async Task OnConnectedAsync()
    {
        sessions.Add(Context); // events go by user id, so a socket whose session ends must be closed (see SessionGuard)
        var userId = Context.User!.UserId();
        if (await presence.ConnectedAsync(userId, Context.ConnectionId, Context.ConnectionAborted))
            await announcer.AnnounceAsync(userId, online: true);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        sessions.Remove(Context.ConnectionId);
        var userId = Context.User!.UserId();
        if (await presence.DisconnectedAsync(userId, Context.ConnectionId))
            await announcer.AnnounceAsync(userId, online: false);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task<MessageDto> SendMessage(Guid conversationId, Guid clientMsgId, string body, Guid? replyToId)
    {
        var ct = Context.ConnectionAborted;
        Fail(await validator.CheckCallerAsync(Context.User!, ct));
        Fail(await validator.CheckSendAsync(Context.User!, conversationId, clientMsgId, body, replyToId, ct));
        return await StoreAndDeliver(conversationId, clientMsgId, CommandValidator.NormalizeBody(body, options.Value.MaxBodyLength)!, replyToId, null, ct);
    }

    /// <summary>A message with 1-10 files uploaded earlier through <c>POST /media</c>, and an optional caption.</summary>
    public async Task<MessageDto> SendMedia(Guid conversationId, Guid clientMsgId, string? caption, List<Guid>? mediaIds, Guid? replyToId)
    {
        var ct = Context.ConnectionAborted;
        Fail(await validator.CheckCallerAsync(Context.User!, ct));
        var files = await validator.CheckSendMediaAsync(Context.User!, conversationId, clientMsgId, caption, replyToId, mediaIds, ct);
        Fail(files.Error);
        return await StoreAndDeliver(conversationId, clientMsgId, CommandValidator.NormalizeBody(caption, options.Value.MaxBodyLength), replyToId, files.Value, ct);
    }

    private async Task<MessageDto> StoreAndDeliver(
        Guid conversationId, Guid clientMsgId, string? body, Guid? replyToId, IReadOnlyList<Chat.Data.Entities.Media>? files, CancellationToken ct)
    {
        if (!limiter.TryAcquire(Context.User!.UserId())) Fail(AuthError.RateLimited);

        var result = await writer.SendAsync(Context.User!.UserId(), conversationId, clientMsgId, body, replyToId, files, ct);
        Fail(result.Error);
        if (result.Value!.Created)
        {
            await presence.ClearTypingAsync(conversationId, Context.User!.UserId(), ct); // sending ends typing; clients clear on message.created
            await broadcaster.PublishCreatedAsync(result.Value.Message, ct);
        }
        return result.Value.Message;
    }

    public async Task<MessageDto> EditMessage(Guid messageId, string body)
    {
        var ct = Context.ConnectionAborted;
        Fail(await validator.CheckCallerAsync(Context.User!, ct));
        var normalized = CommandValidator.NormalizeBody(body, options.Value.MaxBodyLength);
        var found = await validator.CheckMessageAsync(Context.User!.UserId(), messageId, senderOnly: true, ct);
        Fail(found.Error);
        if (normalized is null) Fail(AuthError.Invalid);

        var edited = await writer.EditAsync(found.Value!, normalized!, ct);
        Fail(edited.Error);
        await broadcaster.PublishAsync("message.edited", edited.Value!, edited.Value!.ConversationId, null, ct);
        return edited.Value;
    }

    public async Task<MessageDto> DeleteMessage(Guid messageId)
    {
        var ct = Context.ConnectionAborted;
        Fail(await validator.CheckCallerAsync(Context.User!, ct));
        var found = await validator.CheckMessageAsync(Context.User!.UserId(), messageId, senderOnly: true, ct);
        Fail(found.Error);

        var deleted = await writer.DeleteAsync(found.Value!, ct);
        if (deleted.Changed)
            await broadcaster.PublishAsync("message.deleted", deleted.Message, deleted.Message.ConversationId, null, ct);
        return deleted.Message;
    }

    public async Task React(Guid messageId, string emoji, bool on)
    {
        var ct = Context.ConnectionAborted;
        var userId = Context.User!.UserId();
        Fail(await validator.CheckCallerAsync(Context.User!, ct));
        var found = await validator.CheckMessageAsync(userId, messageId, senderOnly: false, ct);
        Fail(found.Error);
        if (string.IsNullOrWhiteSpace(emoji) || emoji.Length > options.Value.MaxEmojiLength) Fail(AuthError.Invalid);

        var change = await writer.SetReactionAsync(userId, found.Value!, emoji, on, ct);
        Fail(change.Error);
        if (change.Value!.Changed)
            await broadcaster.PublishAsync(
                "message.reaction", new { change.Value.ConversationId, MessageId = messageId, UserId = userId, Emoji = emoji, On = on },
                change.Value.ConversationId, null, ct);
    }

    /// <summary>Marks everything up to <paramref name="upToSeq"/> as delivered to this device's user.</summary>
    public Task MarkDelivered(Guid conversationId, long upToSeq) => AdvanceReceipt(conversationId, upToSeq, read: false);

    /// <summary>Marks everything up to <paramref name="upToSeq"/> as read (which also counts as delivered).</summary>
    public Task MarkRead(Guid conversationId, long upToSeq) => AdvanceReceipt(conversationId, upToSeq, read: true);

    private async Task AdvanceReceipt(Guid conversationId, long upToSeq, bool read)
    {
        var ct = Context.ConnectionAborted;
        var userId = Context.User!.UserId();
        Fail(await validator.CheckCallerAsync(Context.User!, ct));
        var member = await validator.CheckMemberAsync(userId, conversationId, ct);
        Fail(member.Error);
        if (upToSeq < 0 || upToSeq > member.Value!.Conversation.LastSeq) Fail(AuthError.Invalid);

        var receipt = await receipts.AdvanceAsync(userId, conversationId, upToSeq, read, ct);
        if (receipt is not null) await broadcaster.PublishAsync("receipt.updated", receipt, conversationId, null, ct);
    }

    /// <summary>Clients repeat <c>true</c> about every 3 seconds while typing. Over the limit, the signal is dropped, not refused.</summary>
    public async Task SetTyping(Guid conversationId, bool typing)
    {
        var ct = Context.ConnectionAborted;
        var userId = Context.User!.UserId();
        Fail(await validator.CheckCallerAsync(Context.User!, ct));
        Fail((await validator.CheckMemberAsync(userId, conversationId, ct)).Error);
        if (!typingLimiter.TryAcquire(userId)) return;

        if (typing) await presence.SetTypingAsync(conversationId, userId, ct);
        else await presence.ClearTypingAsync(conversationId, userId, ct);
        await broadcaster.PublishPresenceSignalAsync("typing", new { ConversationId = conversationId, UserId = userId, Typing = typing }, conversationId, userId, ct);
    }

    private static void Fail(AuthError? error)
    {
        if (error is not null) throw new HubException(error.Code);
    }
}

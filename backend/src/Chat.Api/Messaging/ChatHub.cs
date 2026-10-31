using Chat.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Chat.Api.Messaging;

/// <summary>Process 4.0 over WebSocket: the entry point for message commands. Failures are HubExceptions carrying the error code.</summary>
[Authorize]
public class ChatHub(
    CommandValidator validator, MessageWriter writer, MessageBroadcaster broadcaster,
    MessageRateLimiter limiter, IOptions<MessagingOptions> options) : Hub
{
    public async Task<MessageDto> SendMessage(Guid conversationId, Guid clientMsgId, string body, Guid? replyToId)
    {
        var ct = Context.ConnectionAborted;
        Fail(await validator.CheckCallerAsync(Context.User!, ct));
        Fail(await validator.CheckSendAsync(Context.User!, conversationId, clientMsgId, body, replyToId, ct));
        if (!limiter.TryAcquire(Context.User!.UserId())) Fail(AuthError.RateLimited);

        var result = await writer.SendAsync(
            Context.User!.UserId(), conversationId, clientMsgId,
            CommandValidator.NormalizeBody(body, options.Value.MaxBodyLength)!, replyToId, ct);
        Fail(result.Error);
        if (result.Value!.Created)
            await broadcaster.PublishAsync("message.created", result.Value.Message, conversationId, null, ct);
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

    private static void Fail(AuthError? error)
    {
        if (error is not null) throw new HubException(error.Code);
    }
}

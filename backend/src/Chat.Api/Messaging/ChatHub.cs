using Chat.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Chat.Api.Messaging;

/// <summary>Process 4.0 over WebSocket: the entry point for message commands. Failures are HubExceptions carrying the error code.</summary>
[Authorize]
public class ChatHub(
    CommandValidator validator, MessageWriter writer, MessageBroadcaster broadcaster, IOptions<MessagingOptions> options) : Hub
{
    public async Task<MessageDto> SendMessage(Guid conversationId, Guid clientMsgId, string body, Guid? replyToId)
    {
        var ct = Context.ConnectionAborted;
        Fail(await validator.CheckSendAsync(Context.User!, conversationId, clientMsgId, body, replyToId, ct));

        var result = await writer.SendAsync(
            Context.User!.UserId(), conversationId, clientMsgId,
            CommandValidator.NormalizeBody(body, options.Value.MaxBodyLength)!, replyToId, ct);
        Fail(result.Error);
        if (result.Value!.Created)
            await broadcaster.PublishAsync("message.created", result.Value.Message, conversationId, null, ct);
        return result.Value.Message;
    }

    private static void Fail(AuthError? error)
    {
        if (error is not null) throw new HubException(error.Code);
    }
}

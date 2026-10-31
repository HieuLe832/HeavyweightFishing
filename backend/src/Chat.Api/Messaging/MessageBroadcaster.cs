using Chat.Api.Realtime;
using Chat.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Messaging;

/// <summary>
/// Process 4.3: sends an event to every device of every active member of a conversation (SignalR, which spreads it over the
/// Redis backplane when there are several nodes) and hands members with no live connection to 4.4 through the event bus.
/// Recipients are looked up per event, so someone who left stops receiving at once and there are no connection groups to keep in sync.
/// </summary>
public class MessageBroadcaster(
    ChatDbContext db, IHubContext<ChatHub> hub, PresenceService presence, IEventBus bus, ILogger<MessageBroadcaster> log)
{
    /// <summary>Call after the transaction commits. A delivery failure is logged, not thrown: the change already happened.</summary>
    public async Task PublishAsync(string eventName, object payload, Guid conversationId, IEnumerable<Guid>? extraRecipients, CancellationToken ct)
    {
        try
        {
            var members = await MembersAsync(conversationId, ct);
            await SendAsync(eventName, payload, members.Concat(extraRecipients ?? []), ct);
        }
        catch (Exception e)
        {
            log.LogError(e, "Could not deliver {Event} for conversation {ConversationId}", eventName, conversationId);
        }
    }

    /// <summary>Like <see cref="PublishAsync"/> but not to <paramref name="exceptUserId"/> (typing goes to the others).</summary>
    public async Task PublishToOthersAsync(string eventName, object payload, Guid conversationId, Guid exceptUserId, CancellationToken ct)
    {
        try
        {
            var members = await MembersAsync(conversationId, ct);
            await SendAsync(eventName, payload, members.Where(id => id != exceptUserId), ct);
        }
        catch (Exception e)
        {
            log.LogError(e, "Could not deliver {Event} for conversation {ConversationId}", eventName, conversationId);
        }
    }

    public async Task PublishToUsersAsync(string eventName, object payload, IEnumerable<Guid> userIds, CancellationToken ct)
    {
        try
        {
            await SendAsync(eventName, payload, userIds, ct);
        }
        catch (Exception e)
        {
            log.LogError(e, "Could not deliver {Event}", eventName);
        }
    }

    /// <summary>
    /// Delivers <c>message.created</c>, then for a user message tells the bus which other members have no live connection.
    /// The queue carries ids, not text: the consumer reads the row, so a delete in between wins.
    /// </summary>
    public async Task PublishCreatedAsync(MessageDto message, CancellationToken ct)
    {
        await PublishAsync("message.created", message, message.ConversationId, null, ct);
        if (message.SenderId is not { } sender || message.Type == "system") return;

        try
        {
            var others = (await MembersAsync(message.ConversationId, ct)).Where(id => id != sender).ToList();
            var online = await presence.OnlineAsync(others, ct);
            var offline = others.Where(id => !online.Contains(id)).ToList();
            if (offline.Count > 0)
                await bus.PublishAsync("message.created", new
                {
                    MessageId = message.Id, message.ConversationId, message.Seq, SenderId = sender, OfflineUserIds = offline,
                }, ct);
        }
        catch (Exception e)
        {
            log.LogError(e, "Could not hand message {MessageId} to the event bus", message.Id);
        }
    }

    private Task<List<Guid>> MembersAsync(Guid conversationId, CancellationToken ct) =>
        db.ConversationMembers.Where(m => m.ConversationId == conversationId && m.LeftAt == null).Select(m => m.UserId).ToListAsync(ct);

    private Task SendAsync(string eventName, object payload, IEnumerable<Guid> userIds, CancellationToken ct) =>
        hub.Clients.Users(userIds.Distinct().Select(id => id.ToString()).ToList()).SendAsync(eventName, payload, ct);
}

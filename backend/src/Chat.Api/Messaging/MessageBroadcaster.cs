using Chat.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Messaging;

/// <summary>
/// Process 4.3, single node: sends an event to every device of every active member of a conversation. Recipients are
/// looked up per event, so someone who left stops receiving at once and there are no connection groups to keep in sync.
/// </summary>
public class MessageBroadcaster(ChatDbContext db, IHubContext<ChatHub> hub, ILogger<MessageBroadcaster> log)
{
    /// <summary>Call after the transaction commits. A delivery failure is logged, not thrown: the change already happened.</summary>
    public async Task PublishAsync(string eventName, object payload, Guid conversationId, IEnumerable<Guid>? extraRecipients, CancellationToken ct)
    {
        try
        {
            var members = await db.ConversationMembers
                .Where(m => m.ConversationId == conversationId && m.LeftAt == null).Select(m => m.UserId).ToListAsync(ct);
            var ids = members.Concat(extraRecipients ?? []).Distinct().Select(id => id.ToString()).ToList();
            await hub.Clients.Users(ids).SendAsync(eventName, payload, ct);
        }
        catch (Exception e)
        {
            log.LogError(e, "Could not deliver {Event} for conversation {ConversationId}", eventName, conversationId);
        }
    }
}

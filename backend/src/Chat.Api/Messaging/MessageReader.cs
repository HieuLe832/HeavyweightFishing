using Chat.Api.Auth;
using Chat.Api.Conversations;
using Chat.Data;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Messaging;

/// <param name="HasMore">Whether more messages exist beyond this page in the direction it was read.</param>
public record MessagePage(List<MessageDto> Messages, bool HasMore);

/// <summary>Process 4.6: pages of D6 for a member, in ascending <c>seq</c>.</summary>
public class MessageReader(ChatDbContext db, ConversationReader conversations)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 50;

    /// <summary>
    /// With no cursor, the latest page. <paramref name="before"/> reads the page just older than a seq;
    /// <paramref name="after"/> reads newer messages, oldest first, which is how a reconnecting client fills its gap.
    /// </summary>
    public async Task<Result<MessagePage>> PageAsync(
        Guid userId, Guid conversationId, long? before, long? after, int limit, CancellationToken ct)
    {
        if (await conversations.FindAsync(userId, conversationId, ct) is null) return AuthError.ConversationNotFound;

        var query = db.Messages.AsNoTracking().Where(m => m.ConversationId == conversationId);
        var rows = after is { } a
            ? await query.Where(m => m.Seq > a).OrderBy(m => m.Seq).Take(limit + 1).ToListAsync(ct)
            : await (before is { } b ? query.Where(m => m.Seq < b) : query).OrderByDescending(m => m.Seq).Take(limit + 1).ToListAsync(ct);

        // The extra row only tells us whether there is more; it is the furthest one in the direction we read.
        var hasMore = rows.Count > limit;
        rows = rows.Take(limit).ToList();
        if (after is null) rows.Reverse();

        var ids = rows.Select(m => m.Id).ToList();
        var counts = await db.MessageReactions.Where(r => ids.Contains(r.MessageId))
            .GroupBy(r => new { r.MessageId, r.Emoji })
            .Select(g => new { g.Key.MessageId, g.Key.Emoji, Count = g.Count(), Mine = g.Any(r => r.UserId == userId) })
            .ToListAsync(ct);
        var byMessage = counts.ToLookup(c => c.MessageId, c => new ReactionCount(c.Emoji, c.Count, c.Mine));

        return new MessagePage(rows.Select(m => MessageWriter.ToDto(m, byMessage[m.Id].ToList())).ToList(), hasMore);
    }
}

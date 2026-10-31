using Chat.Data;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Realtime;

/// <summary>
/// Who may see a user's presence: people who share an active conversation with them, minus anyone with a block either
/// way. A block must look like a missing user, so the blocked person must not see the blocker come and go.
/// </summary>
public class PresenceAudience(ChatDbContext db)
{
    public async Task<List<Guid>> ForUserAsync(Guid userId, CancellationToken ct)
    {
        var peers = await (
            from mine in db.ConversationMembers
            join other in db.ConversationMembers on mine.ConversationId equals other.ConversationId
            where mine.UserId == userId && mine.LeftAt == null && other.UserId != userId && other.LeftAt == null
            select other.UserId).Distinct().ToListAsync(ct);
        return await WithoutBlocksAsync(userId, peers, ct);
    }

    public async Task<List<Guid>> WithoutBlocksAsync(Guid userId, IReadOnlyCollection<Guid> others, CancellationToken ct)
    {
        var blocked = await db.Blocks
            .Where(b => (b.BlockerId == userId && others.Contains(b.BlockedId)) || (b.BlockedId == userId && others.Contains(b.BlockerId)))
            .Select(b => b.BlockerId == userId ? b.BlockedId : b.BlockerId).ToListAsync(ct);
        return others.Except(blocked).ToList();
    }
}

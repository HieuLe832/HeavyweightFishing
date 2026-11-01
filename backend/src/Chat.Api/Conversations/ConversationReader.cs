using Chat.Api.Auth;
using Chat.Data;
using Chat.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Conversations;

/// <summary>A conversation together with the asking user's active membership in it.</summary>
public record Membership(Conversation Conversation, ConversationMember Member);

/// <summary>Reads D5 for one user. Someone who left a conversation can no longer see it.</summary>
public class ConversationReader(ChatDbContext db)
{
    public async Task<Membership?> FindAsync(Guid userId, Guid conversationId, CancellationToken ct)
    {
        var row = await (
            from m in db.ConversationMembers
            join c in db.Conversations on m.ConversationId equals c.Id
            where m.ConversationId == conversationId && m.UserId == userId && m.LeftAt == null
            select new { c, m }).SingleOrDefaultAsync(ct);
        return row is null ? null : new Membership(row.c, row.m);
    }

    public async Task<Result<ConversationDetail>> DetailAsync(Guid userId, Guid conversationId, CancellationToken ct)
    {
        var membership = await FindAsync(userId, conversationId, ct);
        if (membership is null) return AuthError.ConversationNotFound;

        var members = await (
            from m in db.ConversationMembers
            join u in db.Users on m.UserId equals u.Id
            where m.ConversationId == conversationId && m.LeftAt == null
            orderby m.JoinedAt, u.Username
            select new MemberSummary(u.Id, u.Username, u.DisplayName, ConversationRules.Name(m.Role), m.JoinedAt, m.LastDeliveredSeq, m.LastReadSeq, u.AvatarMediaId, u.AvatarPresetKey)
        ).ToListAsync(ct);

        PersonBrief? peer = null;
        if (membership.Conversation.Type == ConversationType.Direct)
        {
            var other = members.FirstOrDefault(m => m.UserId != userId);
            if (other is not null) peer = new PersonBrief(other.UserId, other.Username, other.DisplayName, other.AvatarMediaId, other.AvatarPresetKey);
        }
        return new ConversationDetail(Summarize(membership, peer), members);
    }

    /// <summary>
    /// The user's conversations, most recently active first. For the next page pass the last <c>UpdatedAt</c> as <c>before</c> and its
    /// <c>Id</c> as <c>beforeId</c>: several conversations can share a time, and <c>before</c> alone skips those at the page boundary.
    /// </summary>
    public async Task<List<ConversationSummary>> ListAsync(Guid userId, int limit, DateTimeOffset? before, Guid? beforeId, CancellationToken ct)
    {
        var rows = await (
            from m in db.ConversationMembers
            join c in db.Conversations on m.ConversationId equals c.Id
            where m.UserId == userId && m.LeftAt == null
                && (before == null || c.UpdatedAt < before || (beforeId != null && c.UpdatedAt == before && c.Id.CompareTo(beforeId.Value) > 0))
            orderby c.UpdatedAt descending, c.Id
            select new { c, m }).Take(limit).ToListAsync(ct);

        var directIds = rows.Where(r => r.c.Type == ConversationType.Direct).Select(r => r.c.Id).ToList();
        var peers = await (
            from m in db.ConversationMembers
            join u in db.Users on m.UserId equals u.Id
            where directIds.Contains(m.ConversationId) && m.UserId != userId
            select new { m.ConversationId, Peer = new PersonBrief(u.Id, u.Username, u.DisplayName, u.AvatarMediaId, u.AvatarPresetKey) }
        ).ToDictionaryAsync(x => x.ConversationId, x => x.Peer, ct);

        return rows.Select(r => Summarize(new Membership(r.c, r.m), peers.GetValueOrDefault(r.c.Id))).ToList();
    }

    public static ConversationSummary Summarize(Membership x, PersonBrief? peer) => new(
        x.Conversation.Id, ConversationRules.Name(x.Conversation.Type), x.Conversation.Title, peer,
        x.Conversation.LastSeq, Math.Max(0, x.Conversation.LastSeq - x.Member.LastReadSeq),
        x.Member.MutedUntil, ConversationRules.Name(x.Member.Role), x.Conversation.UpdatedAt, x.Conversation.AvatarMediaId);
}

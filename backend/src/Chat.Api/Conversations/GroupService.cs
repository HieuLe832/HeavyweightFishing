using Chat.Api.Auth;
using Chat.Api.Users;
using Chat.Data;
using Chat.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Chat.Api.Conversations;

/// <summary>Process 3.0: managing a group's title, members and roles.</summary>
public class GroupService(ChatDbContext db, ConversationReader reader, UserAccess access, TimeProvider time)
{
    public Task<AuthError?> RenameAsync(Guid userId, Guid conversationId, string title, CancellationToken ct) =>
        MutateAsync(userId, conversationId, manager: true, ct, m =>
        {
            m.Conversation.Title = title.Trim();
            return Task.FromResult<AuthError?>(null);
        });

    public Task<AuthError?> AddMembersAsync(Guid userId, Guid conversationId, IEnumerable<Guid> userIds, CancellationToken ct) =>
        MutateAsync(userId, conversationId, manager: true, ct, async m =>
        {
            var ids = userIds.Distinct().ToList();
            var found = await access.FindReachableManyAsync(userId, ids, ct);
            if (found.Error is { } error) return error;

            var rows = await db.ConversationMembers.Where(x => x.ConversationId == conversationId && ids.Contains(x.UserId)).ToListAsync(ct);
            var active = await db.ConversationMembers.CountAsync(x => x.ConversationId == conversationId && x.LeftAt == null, ct);
            var now = time.GetUtcNow();
            foreach (var id in ids)
            {
                var row = rows.SingleOrDefault(x => x.UserId == id);
                if (row is { LeftAt: null }) continue;
                if (++active > ConversationRules.MaxMembers) return AuthError.GroupFull;

                // Someone who left starts afresh, with nothing unread from before they came back.
                if (row is null) db.ConversationMembers.Add(row = ConversationService.NewMember(conversationId, id, MemberRole.Member, now));
                row.Role = MemberRole.Member;
                row.JoinedAt = now;
                row.LeftAt = null;
                row.MutedUntil = null;
                row.LastReadSeq = row.LastDeliveredSeq = m.Conversation.LastSeq;
            }
            return null;
        });

    /// <summary>Removes another member. Owners remove anyone; admins remove plain members only.</summary>
    public Task<AuthError?> RemoveMemberAsync(Guid userId, Guid conversationId, Guid targetId, CancellationToken ct) =>
        MutateAsync(userId, conversationId, manager: true, ct, async m =>
        {
            if (targetId == userId) return AuthError.SelfTarget;
            var target = await FindActiveAsync(conversationId, targetId, ct);
            if (target is null) return AuthError.MemberNotFound;
            if (m.Member.Role != MemberRole.Owner && target.Role != MemberRole.Member) return AuthError.InsufficientRole;

            target.LeftAt = time.GetUtcNow();
            return null;
        });

    /// <summary>Leaves the group. If the owner leaves, the longest-standing admin (else member) becomes owner.</summary>
    public Task<AuthError?> LeaveAsync(Guid userId, Guid conversationId, CancellationToken ct) =>
        MutateAsync(userId, conversationId, manager: false, ct, async m =>
        {
            m.Member.LeftAt = time.GetUtcNow();
            if (m.Member.Role != MemberRole.Owner) return null;

            var others = await db.ConversationMembers
                .Where(x => x.ConversationId == conversationId && x.LeftAt == null && x.UserId != userId).ToListAsync(ct);
            var heir = others.OrderBy(x => x.Role == MemberRole.Admin ? 0 : 1).ThenBy(x => x.JoinedAt).FirstOrDefault();
            if (heir is not null) heir.Role = MemberRole.Owner;
            return null;
        });

    /// <summary>Makes a member an admin or a plain member. Only the owner may, and never for themselves.</summary>
    public Task<AuthError?> SetRoleAsync(Guid userId, Guid conversationId, Guid targetId, MemberRole role, CancellationToken ct) =>
        MutateAsync(userId, conversationId, manager: false, ct, async m =>
        {
            if (m.Member.Role != MemberRole.Owner) return AuthError.InsufficientRole;
            if (targetId == userId) return AuthError.SelfTarget;
            var target = await FindActiveAsync(conversationId, targetId, ct);
            if (target is null) return AuthError.MemberNotFound;

            target.Role = role;
            return null;
        });

    private Task<ConversationMember?> FindActiveAsync(Guid conversationId, Guid userId, CancellationToken ct) =>
        db.ConversationMembers.SingleOrDefaultAsync(x => x.ConversationId == conversationId && x.UserId == userId && x.LeftAt == null, ct);

    /// <summary>
    /// Runs a change to a group under a lock on its row, so two changes to the same group queue up instead of
    /// racing (two admins removing each other, the owner leaving while someone is added).
    /// </summary>
    private async Task<AuthError?> MutateAsync(
        Guid userId, Guid conversationId, bool manager, CancellationToken ct, Func<Membership, Task<AuthError?>> change)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM conversations WHERE id = {conversationId} FOR UPDATE", ct);

        var membership = await reader.FindAsync(userId, conversationId, ct);
        if (membership is null) return AuthError.ConversationNotFound;
        if (membership.Conversation.Type != ConversationType.Group) return AuthError.NotAGroup;
        if (manager && !ConversationRules.CanManage(membership.Member.Role)) return AuthError.InsufficientRole;

        if (await change(membership) is { } error) return error;

        membership.Conversation.UpdatedAt = time.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return AuthError.Conflict;
        }
        await tx.CommitAsync(ct);
        return null;
    }
}

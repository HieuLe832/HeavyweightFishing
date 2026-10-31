using Chat.Api.Auth;
using Chat.Api.Users;
using Chat.Data;
using Chat.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Chat.Api.Conversations;

/// <summary>Process 3.0: creating conversations, listing them, and muting.</summary>
public class ConversationService(
    ChatDbContext db, ConversationReader reader, UserAccess access, TimeProvider time)
{
    /// <summary>Opens the direct chat with a user, or returns the one that already exists.</summary>
    public async Task<Result<(ConversationDetail Detail, bool Created)>> OpenDirectAsync(
        Guid userId, Guid otherId, CancellationToken ct)
    {
        var found = await access.FindReachableAsync(userId, otherId, ct);
        if (found.Error is { } error) return error;

        var key = DirectKey(userId, otherId);
        var existing = await db.Conversations.Where(c => c.DirectKey == key).Select(c => c.Id).SingleOrDefaultAsync(ct);
        var created = existing == Guid.Empty;
        if (created)
        {
            var now = time.GetUtcNow();
            var conversation = new Conversation
            {
                Id = Guid.NewGuid(), Type = ConversationType.Direct, DirectKey = key, CreatedAt = now, UpdatedAt = now,
            };
            db.Conversations.Add(conversation);
            db.ConversationMembers.AddRange(
                NewMember(conversation.Id, userId, MemberRole.Member, now),
                NewMember(conversation.Id, otherId, MemberRole.Member, now));
            try
            {
                await db.SaveChangesAsync(ct);
                existing = conversation.Id;
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // The other side opened the same chat at the same moment; use theirs.
                db.ChangeTracker.Clear();
                existing = await db.Conversations.Where(c => c.DirectKey == key).Select(c => c.Id).SingleAsync(ct);
                created = false;
            }
        }

        var detail = await reader.DetailAsync(userId, existing, ct);
        return (detail.Value!, created);
    }

    public async Task<Result<ConversationDetail>> CreateGroupAsync(Guid userId, CreateGroupRequest request, CancellationToken ct)
    {
        var ids = request.MemberIds!.Distinct().ToList();
        var found = await access.FindReachableManyAsync(userId, ids, ct);
        if (found.Error is { } error) return error;

        var now = time.GetUtcNow();
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(), Type = ConversationType.Group, Title = request.Title!.Trim(), CreatedAt = now, UpdatedAt = now,
        };
        db.Conversations.Add(conversation);
        db.ConversationMembers.Add(NewMember(conversation.Id, userId, MemberRole.Owner, now));
        db.ConversationMembers.AddRange(ids.Select(id => NewMember(conversation.Id, id, MemberRole.Member, now)));
        await db.SaveChangesAsync(ct);
        return await reader.DetailAsync(userId, conversation.Id, ct);
    }

    public async Task<AuthError?> MuteAsync(Guid userId, Guid conversationId, DateTimeOffset? until, CancellationToken ct)
    {
        var membership = await reader.FindAsync(userId, conversationId, ct);
        if (membership is null) return AuthError.ConversationNotFound;

        membership.Member.MutedUntil = until;
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>The two ids, lower-case and sorted, so both sides compute the same key.</summary>
    public static string DirectKey(Guid a, Guid b)
    {
        var (x, y) = (a.ToString("D"), b.ToString("D"));
        return string.CompareOrdinal(x, y) <= 0 ? $"{x}:{y}" : $"{y}:{x}";
    }

    internal static ConversationMember NewMember(Guid conversationId, Guid userId, MemberRole role, DateTimeOffset now) =>
        new() { ConversationId = conversationId, UserId = userId, Role = role, JoinedAt = now };
}

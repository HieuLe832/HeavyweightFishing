using Chat.Api.Auth;
using Chat.Data;
using Chat.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Chat.Api.Users;

/// <summary>Process 2.0: the contact list (D4) and blocking.</summary>
public class ContactService(ChatDbContext db, UserAccess access, TimeProvider time)
{
    public async Task<List<ContactSummary>> ListContactsAsync(Guid ownerId, CancellationToken ct) =>
        await db.Contacts
            .Where(c => c.OwnerId == ownerId)
            .Join(db.Users, c => c.ContactId, u => u.Id, (c, u) => new { c, u })
            .Where(x => x.u.Status == UserStatus.Active
                && !db.Blocks.Any(b => b.BlockerId == x.u.Id && b.BlockedId == ownerId))
            .OrderBy(x => (x.c.Nickname ?? x.u.DisplayName).ToLower())
            .Select(x => new ContactSummary(x.u.Id, x.u.Username, x.u.DisplayName, x.c.Nickname, x.c.CreatedAt))
            .ToListAsync(ct);

    /// <summary>Adds the contact, or changes the nickname of an existing one.</summary>
    public async Task<Result<ContactSummary>> SaveContactAsync(
        Guid ownerId, Guid contactId, ContactRequest? request, CancellationToken ct)
    {
        var found = await access.FindReachableAsync(ownerId, contactId, ct);
        if (found.Error is { } error) return error;
        var user = found.Value!;
        var nickname = request?.Nickname?.Trim();

        for (var attempt = 0; ; attempt++)
        {
            var contact = await db.Contacts.SingleOrDefaultAsync(c => c.OwnerId == ownerId && c.ContactId == contactId, ct);
            if (contact is null)
                db.Contacts.Add(contact = new Contact { OwnerId = ownerId, ContactId = contactId, CreatedAt = time.GetUtcNow() });
            contact.Nickname = nickname;
            try
            {
                await db.SaveChangesAsync(ct);
                return new ContactSummary(user.Id, user.Username, user.DisplayName, contact.Nickname, contact.CreatedAt);
            }
            catch (DbUpdateException e) when (attempt == 0 && IsUniqueViolation(e))
            {
                // A parallel request added the same contact first; go round again and update it.
                db.ChangeTracker.Clear();
            }
        }
    }

    public async Task RemoveContactAsync(Guid ownerId, Guid contactId, CancellationToken ct) =>
        await db.Contacts.Where(c => c.OwnerId == ownerId && c.ContactId == contactId).ExecuteDeleteAsync(ct);

    public async Task<List<BlockSummary>> ListBlocksAsync(Guid blockerId, CancellationToken ct) =>
        await db.Blocks
            .Where(b => b.BlockerId == blockerId)
            .Join(db.Users, b => b.BlockedId, u => u.Id, (b, u) => new { b, u })
            .OrderByDescending(x => x.b.CreatedAt)
            .Select(x => new BlockSummary(x.u.Id, x.u.Username, x.u.DisplayName, x.b.CreatedAt))
            .ToListAsync(ct);

    /// <summary>Blocks the user and drops them from the blocker's contacts. Blocking twice is fine.</summary>
    public async Task<AuthError?> BlockAsync(Guid blockerId, Guid blockedId, CancellationToken ct)
    {
        if (blockedId == blockerId) return AuthError.SelfTarget;
        if (!await db.Users.AnyAsync(u => u.Id == blockedId, ct)) return AuthError.UserNotFound;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Contacts.Where(c => c.OwnerId == blockerId && c.ContactId == blockedId).ExecuteDeleteAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO blocks (blocker_id, blocked_id, created_at)
            VALUES ({blockerId}, {blockedId}, {time.GetUtcNow()})
            ON CONFLICT DO NOTHING
            """, ct);
        await tx.CommitAsync(ct);
        return null;
    }

    public async Task UnblockAsync(Guid blockerId, Guid blockedId, CancellationToken ct) =>
        await db.Blocks.Where(b => b.BlockerId == blockerId && b.BlockedId == blockedId).ExecuteDeleteAsync(ct);

    private static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}

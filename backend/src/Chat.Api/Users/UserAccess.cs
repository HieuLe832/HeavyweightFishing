using Chat.Api.Auth;
using Chat.Data;
using Chat.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Users;

/// <summary>
/// Who a signed-in user may see and reach. A user who blocked the viewer looks exactly like a missing one,
/// so a block does not tell the blocked person where they stand.
/// </summary>
public class UserAccess(ChatDbContext db)
{
    /// <summary>An active user other than the viewer who has not blocked the viewer.</summary>
    public async Task<Result<User>> FindVisibleAsync(Guid viewerId, Guid targetId, CancellationToken ct)
    {
        if (targetId == viewerId) return AuthError.SelfTarget;
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == targetId && u.Status == UserStatus.Active, ct);
        if (user is null || await HasBlockedAsync(targetId, viewerId, ct)) return AuthError.UserNotFound;
        return user;
    }

    /// <summary>Visible, and not someone the viewer blocked: the people the viewer can start talking to.</summary>
    public async Task<Result<User>> FindReachableAsync(Guid viewerId, Guid targetId, CancellationToken ct)
    {
        var result = await FindVisibleAsync(viewerId, targetId, ct);
        if (result.Error is not null) return result;
        return await HasBlockedAsync(viewerId, targetId, ct) ? AuthError.Blocked : result;
    }

    /// <summary>
    /// <see cref="FindReachableAsync"/> for several users at once. All or nothing: the first problem found is
    /// the answer, and users who blocked the viewer count as missing.
    /// </summary>
    public async Task<Result<List<User>>> FindReachableManyAsync(Guid viewerId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Contains(viewerId)) return AuthError.SelfTarget;
        var users = await db.Users.Where(u => ids.Contains(u.Id) && u.Status == UserStatus.Active).ToListAsync(ct);
        if (users.Count != ids.Count) return AuthError.UserNotFound;

        var blockedViewer = await db.Blocks.AnyAsync(b => ids.Contains(b.BlockerId) && b.BlockedId == viewerId, ct);
        if (blockedViewer) return AuthError.UserNotFound;
        var viewerBlocked = await db.Blocks.AnyAsync(b => b.BlockerId == viewerId && ids.Contains(b.BlockedId), ct);
        return viewerBlocked ? AuthError.Blocked : users;
    }

    public Task<bool> HasBlockedAsync(Guid blockerId, Guid blockedId, CancellationToken ct) =>
        db.Blocks.AnyAsync(b => b.BlockerId == blockerId && b.BlockedId == blockedId, ct);
}

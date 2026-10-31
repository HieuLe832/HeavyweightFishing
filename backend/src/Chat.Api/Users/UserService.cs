using Chat.Api.Auth;
using Chat.Data;
using Chat.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Users;

/// <summary>Process 2.0: own profile, looking users up and searching them.</summary>
public class UserService(ChatDbContext db, UserAccess access, TimeProvider time)
{
    public async Task<UserSummary?> UpdateProfileAsync(Guid userId, UpdateProfileRequest request, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return null;

        user.DisplayName = request.DisplayName!.Trim();
        user.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return SignInService.Summary(user);
    }

    public async Task<Result<UserProfile>> GetAsync(Guid viewerId, Guid targetId, CancellationToken ct)
    {
        // Looking at yourself is fine here; every other rule is the same as for contacts and chats.
        var found = targetId == viewerId
            ? await db.Users.SingleOrDefaultAsync(u => u.Id == viewerId && u.Status == UserStatus.Active, ct)
            : (await access.FindVisibleAsync(viewerId, targetId, ct)).Value;
        if (found is null) return AuthError.UserNotFound;

        return await db.Users.Where(u => u.Id == targetId).Select(Profile(viewerId)).SingleAsync(ct);
    }

    /// <summary>Active users whose username starts with, or display name contains, the text. Never the viewer.</summary>
    public async Task<List<UserProfile>> SearchAsync(Guid viewerId, string q, int limit, CancellationToken ct)
    {
        var text = Escape(q.Trim());
        return await db.Users
            .Where(u => u.Status == UserStatus.Active && u.Id != viewerId
                && !db.Blocks.Any(b => b.BlockerId == u.Id && b.BlockedId == viewerId)
                && (EF.Functions.ILike(u.Username, text + "%", "\\") || EF.Functions.ILike(u.DisplayName, "%" + text + "%", "\\")))
            .OrderBy(u => u.Username)
            .Take(limit)
            .Select(Profile(viewerId))
            .ToListAsync(ct);
    }

    private System.Linq.Expressions.Expression<Func<User, UserProfile>> Profile(Guid viewerId) => u => new UserProfile(
        u.Id, u.Username, u.DisplayName,
        db.Contacts.Any(c => c.OwnerId == viewerId && c.ContactId == u.Id),
        db.Blocks.Any(b => b.BlockerId == viewerId && b.BlockedId == u.Id));

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}

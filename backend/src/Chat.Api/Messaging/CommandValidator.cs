using System.Security.Claims;
using Chat.Api.Auth;
using Chat.Api.Conversations;
using Chat.Api.Users;
using Chat.Data;
using Chat.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chat.Api.Messaging;

/// <summary>Process 4.1: decides whether a caller may do something in a conversation, before 4.2 stores anything.</summary>
public class CommandValidator(ChatDbContext db, ConversationReader reader, UserAccess access, IOptions<MessagingOptions> options, TimeProvider time)
{
    /// <summary>The trimmed body, or null when it is empty or longer than <paramref name="max"/>.</summary>
    public static string? NormalizeBody(string? body, int max)
    {
        var trimmed = body?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > max ? null : trimmed;
    }

    /// <summary>
    /// A hub connection outlives its access token, so every command re-checks that the session is still live and the
    /// user still active. This is what makes logout and bans stop an open socket.
    /// </summary>
    public async Task<AuthError?> CheckCallerAsync(ClaimsPrincipal caller, CancellationToken ct)
    {
        var userId = caller.UserId();
        var sessionId = caller.SessionId();
        var now = time.GetUtcNow();
        var live = await db.Sessions.AnyAsync(s =>
            s.Id == sessionId && s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now
            && db.Users.Any(u => u.Id == userId && u.Status == UserStatus.Active), ct);
        return live ? null : AuthError.SessionEnded;
    }

    public async Task<AuthError?> CheckSendAsync(
        ClaimsPrincipal caller, Guid conversationId, Guid clientMsgId, string? body, Guid? replyToId, CancellationToken ct)
    {
        if (clientMsgId == Guid.Empty || NormalizeBody(body, options.Value.MaxBodyLength) is null) return AuthError.Invalid;

        var member = await CheckMemberAsync(caller.UserId(), conversationId, ct);
        if (member.Error is not null) return member.Error;

        if (replyToId is { } reply && !await db.Messages.AnyAsync(m => m.Id == reply && m.ConversationId == conversationId, ct))
            return AuthError.Invalid;
        return null;
    }

    /// <summary>
    /// The caller is an active member. In a direct chat, a block either way refuses the command, and a block by
    /// the other person looks like a missing user (<see cref="UserAccess"/>).
    /// </summary>
    public async Task<Result<Membership>> CheckMemberAsync(Guid userId, Guid conversationId, CancellationToken ct)
    {
        var membership = await reader.FindAsync(userId, conversationId, ct);
        if (membership is null) return AuthError.ConversationNotFound;
        if (membership.Conversation.Type != ConversationType.Direct) return membership;

        var otherId = await db.ConversationMembers
            .Where(m => m.ConversationId == conversationId && m.UserId != userId && m.LeftAt == null)
            .Select(m => (Guid?)m.UserId).FirstOrDefaultAsync(ct);
        if (otherId is not { } other) return membership;

        if (await access.HasBlockedAsync(other, userId, ct)) return AuthError.UserNotFound;
        if (await access.HasBlockedAsync(userId, other, ct)) return AuthError.Blocked;
        return membership;
    }
}

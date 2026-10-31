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

        return await CheckDestinationAsync(caller.UserId(), conversationId, replyToId, ct);
    }

    /// <summary>
    /// A message with files. The caption is optional; 1-10 distinct files, each ready and uploaded by the caller. A file that
    /// is missing, someone else's or not ready is <c>invalid_attachment</c> in every case, so ids cannot be probed.
    /// Returns the files in the order given.
    /// </summary>
    public async Task<Result<List<Media>>> CheckSendMediaAsync(
        ClaimsPrincipal caller, Guid conversationId, Guid clientMsgId, string? caption, Guid? replyToId,
        IReadOnlyList<Guid>? mediaIds, CancellationToken ct)
    {
        var o = options.Value;
        if (clientMsgId == Guid.Empty || mediaIds is null || mediaIds.Count == 0 || mediaIds.Count > o.MaxAttachments
            || mediaIds.Distinct().Count() != mediaIds.Count
            || (!string.IsNullOrWhiteSpace(caption) && NormalizeBody(caption, o.MaxBodyLength) is null))
            return AuthError.Invalid;

        if (await CheckDestinationAsync(caller.UserId(), conversationId, replyToId, ct) is { } error) return error;

        var userId = caller.UserId();
        var found = await db.Media.AsNoTracking()
            .Where(m => mediaIds.Contains(m.Id) && m.UploaderId == userId && m.Status == MediaStatus.Ready).ToListAsync(ct);
        if (found.Count != mediaIds.Count) return AuthError.InvalidAttachment;
        return mediaIds.Select(id => found.Single(m => m.Id == id)).ToList();
    }

    private async Task<AuthError?> CheckDestinationAsync(Guid userId, Guid conversationId, Guid? replyToId, CancellationToken ct)
    {
        var member = await CheckMemberAsync(userId, conversationId, ct);
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

    /// <summary>
    /// A user message the caller may act on: it exists, is not a system notice, and the caller is an active member
    /// (a non-member or a member who left gets the same <c>message_not_found</c> as a missing id, so ids cannot be probed).
    /// With <paramref name="senderOnly"/>, it must also be the caller's own. The block rules of <see cref="CheckMemberAsync"/> apply.
    /// The returned message is tracked, ready for <see cref="MessageWriter"/>.
    /// </summary>
    public async Task<Result<Message>> CheckMessageAsync(Guid userId, Guid messageId, bool senderOnly, CancellationToken ct)
    {
        var message = await db.Messages.SingleOrDefaultAsync(m => m.Id == messageId && m.Type != MessageType.System, ct);
        if (message is null) return AuthError.MessageNotFound;

        var member = await CheckMemberAsync(userId, message.ConversationId, ct);
        if (member.Error == AuthError.ConversationNotFound) return AuthError.MessageNotFound;
        if (member.Error is { } error) return error;
        return senderOnly && message.SenderId != userId ? AuthError.MessageNotFound : message;
    }
}

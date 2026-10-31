using Chat.Api.Auth;
using Chat.Data;
using Chat.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Chat.Api.Messaging;

/// <param name="Created">False when the send was a retry and the original message came back.</param>
public record SendResult(MessageDto Message, bool Created);

/// <summary>Process 4.2: the only writer of D6.</summary>
public class MessageWriter(ChatDbContext db, TimeProvider time)
{
    public static MessageDto ToDto(Message m, IReadOnlyList<ReactionCount>? reactions = null) => new(
        m.Id, m.ConversationId, m.Seq, m.SenderId, m.ClientMsgId, m.Type.ToString().ToLowerInvariant(), m.Body,
        m.ReplyToId, m.CreatedAt, m.EditedAt, m.DeletedAt, reactions?.ToList() ?? []);

    /// <summary>
    /// Stores a text message. <c>seq</c> is taken under a lock on the conversation row (see docs/pitfalls.md), and a
    /// retry with the same <paramref name="clientMsgId"/> returns the original instead of storing a second copy.
    /// </summary>
    public async Task<Result<SendResult>> SendAsync(
        Guid senderId, Guid conversationId, Guid clientMsgId, string body, Guid? replyToId, CancellationToken ct)
    {
        if (await FindExistingAsync(senderId, conversationId, clientMsgId, ct) is { } existing) return existing;

        // The validator already loaded this conversation in this scope; its last_seq is stale once we hold the lock.
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM conversations WHERE id = {conversationId} FOR UPDATE", ct);
        // A concurrent retry may have stored it while we waited for the lock.
        if (await FindExistingAsync(senderId, conversationId, clientMsgId, ct) is { } raced) return raced;

        var now = time.GetUtcNow();
        var conversation = await db.Conversations.SingleAsync(c => c.Id == conversationId, ct);
        conversation.LastSeq++;
        conversation.UpdatedAt = now;
        var message = new Message
        {
            Id = Guid.NewGuid(), ConversationId = conversationId, Seq = conversation.LastSeq, SenderId = senderId,
            ClientMsgId = clientMsgId, Type = MessageType.Text, Body = body, ReplyToId = replyToId, CreatedAt = now,
        };
        db.Messages.Add(message);

        // The sender has read their own message; everyone else's cursors wait for 4.5.
        var member = await db.ConversationMembers.SingleAsync(m => m.ConversationId == conversationId && m.UserId == senderId, ct);
        member.LastReadSeq = member.LastDeliveredSeq = message.Seq;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The same client id in another conversation raced us; the unique key is per sender.
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var found = await FindExistingAsync(senderId, conversationId, clientMsgId, ct);
            return found ?? Result<SendResult>.FromError(AuthError.Conflict);
        }
        await tx.CommitAsync(ct);
        return new SendResult(ToDto(message), true);
    }

    /// <summary>The sender's earlier message with this client id: that message in the same conversation, else a conflict.</summary>
    private async Task<Result<SendResult>?> FindExistingAsync(Guid senderId, Guid conversationId, Guid clientMsgId, CancellationToken ct)
    {
        var existing = await db.Messages.AsNoTracking().SingleOrDefaultAsync(m => m.SenderId == senderId && m.ClientMsgId == clientMsgId, ct);
        if (existing is null) return null;
        return existing.ConversationId == conversationId ? new SendResult(ToDto(existing), false) : AuthError.Conflict;
    }
}

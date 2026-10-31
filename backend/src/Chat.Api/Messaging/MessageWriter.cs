using Chat.Api.Auth;
using Chat.Data;
using Chat.Data.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace Chat.Api.Messaging;

/// <param name="Created">False when the send was a retry and the original message came back.</param>
public record SendResult(MessageDto Message, bool Created);

/// <param name="Changed">False when the message was already deleted.</param>
public record DeleteResult(MessageDto Message, bool Changed);

/// <param name="Changed">False when the reaction was already in the requested state.</param>
public record ReactionChange(Guid ConversationId, bool Changed);

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

    private static readonly JsonSerializerOptions NoticeJson = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// Adds a system notice (a group change) to <paramref name="conversation"/> without saving it. The caller already holds the
    /// conversation lock and saves in the same transaction as the change, so the notice and the change succeed or fail together.
    /// The body is data (<c>kind</c>, ids, <c>title</c>), not display text: clients render it.
    /// </summary>
    public Message WriteSystem(Conversation conversation, string kind, Guid? actorId, Guid? userId, string? title = null)
    {
        var now = time.GetUtcNow();
        conversation.LastSeq++;
        var notice = new Message
        {
            Id = Guid.NewGuid(), ConversationId = conversation.Id, Seq = conversation.LastSeq, SenderId = null,
            ClientMsgId = Guid.NewGuid(), Type = MessageType.System, CreatedAt = now,
            Body = JsonSerializer.Serialize(new { kind, actorId, userId, title }, NoticeJson),
        };
        db.Messages.Add(notice);
        return notice;
    }

    /// <summary>Replaces the body of the caller's own message, which <see cref="CommandValidator.CheckMessageAsync"/> returned.</summary>
    public async Task<Result<MessageDto>> EditAsync(Message message, string body, CancellationToken ct)
    {
        if (message.DeletedAt is not null) return AuthError.MessageDeleted;

        // A delete may have committed since the message was loaded; the condition keeps deleted text from coming back.
        var now = time.GetUtcNow();
        var updated = await db.Messages.Where(m => m.Id == message.Id && m.DeletedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Body, body).SetProperty(m => m.EditedAt, now), ct);
        if (updated == 0) return AuthError.MessageDeleted;

        await db.Entry(message).ReloadAsync(ct);
        return ToDto(message, await ReactionCountsAsync(message.Id, ct));
    }

    /// <summary>
    /// Soft delete: the row and its <c>seq</c> stay so history has no gaps, but the text is gone from the database.
    /// Reports keep their own snapshot as evidence (6.1). Deleting twice is a no-op.
    /// </summary>
    public async Task<DeleteResult> DeleteAsync(Message message, CancellationToken ct)
    {
        var changed = message.DeletedAt is null;
        if (changed)
        {
            message.Body = null;
            message.DeletedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return new DeleteResult(ToDto(message, await ReactionCountsAsync(message.Id, ct)), changed);
    }

    /// <summary>
    /// Adds or removes the caller's reaction, which is a state rather than a toggle so a retry cannot flip it.
    /// <paramref name="message"/> comes from <see cref="CommandValidator.CheckMessageAsync"/>.
    /// </summary>
    public async Task<Result<ReactionChange>> SetReactionAsync(Guid userId, Message message, string emoji, bool on, CancellationToken ct)
    {
        if (message.DeletedAt is not null) return AuthError.MessageDeleted;
        var exists = await db.MessageReactions.AnyAsync(r => r.MessageId == message.Id && r.UserId == userId && r.Emoji == emoji, ct);
        if (exists == on) return new ReactionChange(message.ConversationId, false);

        if (!on)
        {
            var removed = await db.MessageReactions
                .Where(r => r.MessageId == message.Id && r.UserId == userId && r.Emoji == emoji).ExecuteDeleteAsync(ct);
            return new ReactionChange(message.ConversationId, removed > 0);
        }

        db.MessageReactions.Add(new MessageReaction { MessageId = message.Id, UserId = userId, Emoji = emoji, CreatedAt = time.GetUtcNow() });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new ReactionChange(message.ConversationId, false); // the same reaction arrived at the same moment
        }
        return new ReactionChange(message.ConversationId, true);
    }

    /// <summary>Counts per emoji for events. <c>Mine</c> is false: an event goes to everyone, so it cannot say whose it is.</summary>
    private Task<List<ReactionCount>> ReactionCountsAsync(Guid messageId, CancellationToken ct) =>
        db.MessageReactions.Where(r => r.MessageId == messageId).GroupBy(r => r.Emoji)
            .Select(g => new ReactionCount(g.Key, g.Count(), false)).ToListAsync(ct);

    /// <summary>The sender's earlier message with this client id: that message in the same conversation, else a conflict.</summary>
    private async Task<Result<SendResult>?> FindExistingAsync(Guid senderId, Guid conversationId, Guid clientMsgId, CancellationToken ct)
    {
        var existing = await db.Messages.AsNoTracking().SingleOrDefaultAsync(m => m.SenderId == senderId && m.ClientMsgId == clientMsgId, ct);
        if (existing is null) return null;
        return existing.ConversationId == conversationId ? new SendResult(ToDto(existing), false) : AuthError.Conflict;
    }
}

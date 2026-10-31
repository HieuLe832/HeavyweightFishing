using Chat.Api.Auth;
using Chat.Data;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Messaging;

/// <summary>A member's cursors after a change, as sent in <c>receipt.updated</c>.</summary>
public record ReceiptDto(Guid ConversationId, Guid UserId, long LastDeliveredSeq, long LastReadSeq);

/// <summary>Process 4.5, receipts: moves a member's read and delivered cursors in D5. They only go forward.</summary>
public class ReceiptService(ChatDbContext db)
{
    /// <summary>
    /// Moves the cursor up to <paramref name="upToSeq"/>. Reading implies delivery. Returns null when nothing moved
    /// (a repeated or stale call), so the caller sends no event. The condition is in the UPDATE, so two devices racing
    /// cannot move a cursor backwards.
    /// </summary>
    public async Task<ReceiptDto?> AdvanceAsync(Guid userId, Guid conversationId, long upToSeq, bool read, CancellationToken ct)
    {
        var members = db.ConversationMembers.Where(m => m.ConversationId == conversationId && m.UserId == userId && m.LeftAt == null);
        var moved = read
            ? await members.Where(m => m.LastReadSeq < upToSeq).ExecuteUpdateAsync(u => u
                .SetProperty(m => m.LastReadSeq, upToSeq)
                .SetProperty(m => m.LastDeliveredSeq, m => m.LastDeliveredSeq > upToSeq ? m.LastDeliveredSeq : upToSeq), ct)
            : await members.Where(m => m.LastDeliveredSeq < upToSeq).ExecuteUpdateAsync(u => u
                .SetProperty(m => m.LastDeliveredSeq, upToSeq), ct);
        if (moved == 0) return null;

        return await members.AsNoTracking()
            .Select(m => new ReceiptDto(m.ConversationId, m.UserId, m.LastDeliveredSeq, m.LastReadSeq)).SingleAsync(ct);
    }
}

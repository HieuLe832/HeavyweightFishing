namespace Chat.Data.Entities;

// D5 Conversations & Members

public class Conversation
{
    public Guid Id { get; set; }
    public ConversationType Type { get; set; }
    public string? Title { get; set; }
    public Guid? AvatarMediaId { get; set; }

    /// <summary>The two member ids, sorted. Set for direct chats, null for groups.</summary>
    public string? DirectKey { get; set; }

    /// <summary>
    /// Highest <see cref="Message.Seq"/> handed out. Lock the row, increment, and use the result;
    /// never derive the next seq from MAX(seq).
    /// </summary>
    public long LastSeq { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class ConversationMember
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public MemberRole Role { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }
    public DateTimeOffset? MutedUntil { get; set; }
    public long LastDeliveredSeq { get; set; }
    public long LastReadSeq { get; set; }
}

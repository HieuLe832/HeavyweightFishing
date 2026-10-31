namespace Chat.Data.Entities;

// D6 Messages & Reactions

public class Message
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public long Seq { get; set; }

    /// <summary>Null for system messages.</summary>
    public Guid? SenderId { get; set; }

    public Guid ClientMsgId { get; set; }
    public MessageType Type { get; set; }
    public string? Body { get; set; }
    public Guid? ReplyToId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public class MessageReaction
{
    public Guid MessageId { get; set; }
    public Guid UserId { get; set; }
    public required string Emoji { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class MessageAttachment
{
    public Guid MessageId { get; set; }
    public Guid MediaId { get; set; }
    public short Position { get; set; }
}

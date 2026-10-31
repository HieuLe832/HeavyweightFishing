namespace Chat.Api.Messaging;

public record ReactionCount(string Emoji, int Count, bool Mine);

/// <summary>A file attached to a message. Fetch the bytes from <c>GET /media/{mediaId}</c>.</summary>
public record AttachmentDto(Guid MediaId, string MimeType, long SizeBytes, int? Width, int? Height);

/// <summary>A message as clients see it, in hub results, events and history. <c>Type</c> is the lower-case enum name.</summary>
public record MessageDto(
    Guid Id, Guid ConversationId, long Seq, Guid? SenderId, Guid ClientMsgId, string Type, string? Body,
    Guid? ReplyToId, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt, DateTimeOffset? DeletedAt,
    List<ReactionCount> Reactions, List<AttachmentDto> Attachments);

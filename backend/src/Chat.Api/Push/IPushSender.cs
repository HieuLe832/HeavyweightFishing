using Chat.Data;

namespace Chat.Api.Push;

/// <summary>What a device is shown. The ids let the app open the right chat.</summary>
public record PushMessage(string Title, string Body, Guid ConversationId, Guid MessageId, long Seq);

public enum PushResult
{
    Sent,

    /// <summary>The provider says the token is dead; the dispatcher clears it.</summary>
    InvalidToken,
}

/// <summary>Delivers a notification to one device through one provider (FCM, APNs or Web Push).</summary>
public interface IPushSender
{
    PushPlatform Platform { get; }

    Task<PushResult> SendAsync(PushMessage message, string token, CancellationToken ct);
}

using Chat.Data;

namespace Chat.Api.Push;

/// <summary>
/// The default sender: logs and sends nothing. Replace it with a real provider adapter (credentials come from the operator).
/// The text of the notification is not logged.
/// </summary>
public class LoggingPushSender(PushPlatform platform, ILogger<LoggingPushSender> log) : IPushSender
{
    public PushPlatform Platform => platform;

    public Task<PushResult> SendAsync(PushMessage message, string token, CancellationToken ct)
    {
        log.LogInformation("Push ({Platform}) for message {MessageId} in conversation {ConversationId}: no provider configured, not sent",
            platform, message.MessageId, message.ConversationId);
        return Task.FromResult(PushResult.Sent);
    }
}

using Chat.Api.Realtime;

namespace Chat.Api.Messaging;

/// <summary>Process 4.5: tells the people who may see a user's presence that it changed.</summary>
public class PresenceAnnouncer(PresenceAudience audience, MessageBroadcaster broadcaster, ILogger<PresenceAnnouncer> log)
{
    public async Task AnnounceAsync(Guid userId, bool online)
    {
        try
        {
            var recipients = await audience.ForUserAsync(userId, CancellationToken.None);
            await broadcaster.PublishToUsersAsync("presence.changed", new { UserId = userId, Online = online }, recipients, CancellationToken.None);
        }
        catch (Exception e)
        {
            log.LogError(e, "Could not announce presence of {UserId}", userId);
        }
    }
}

using System.Text.Json;
using Chat.Api.Realtime;
using Chat.Data;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Push;

/// <summary>
/// Process 4.4: turns "these members were offline for this message" (from 4.3) into notifications. Everything is
/// re-checked here because the event may have waited in a queue. Best effort: a failed device is logged, and only an
/// infrastructure error (which dead-letters the event) escapes.
/// </summary>
public class PushDispatcher(IServiceScopeFactory scopes, IEnumerable<IPushSender> senders, PresenceService presence, TimeProvider time, ILogger<PushDispatcher> log) : IBusHandler
{
    public const int PreviewLength = 100;
    public const string AttachmentPreview = "Sent an attachment";

    private record Event(Guid MessageId, Guid ConversationId, long Seq, Guid SenderId, List<Guid> OfflineUserIds);

    public string Name => "push";
    public string RoutingKey => "message.created";

    public async Task HandleAsync(ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var evt = JsonSerializer.Deserialize<Event>(body.Span, BusJson.Options);
        if (evt is null || evt.OfflineUserIds.Count == 0) return;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var now = time.GetUtcNow();

        var message = await db.Messages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == evt.MessageId, ct);
        if (message is null || message.DeletedAt is not null || message.Type == MessageType.System) return;

        var conversation = await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == evt.ConversationId, ct);
        var sender = await db.Users.AsNoTracking().Where(u => u.Id == evt.SenderId).Select(u => u.DisplayName).SingleOrDefaultAsync(ct);

        var recipients = await (
            from m in db.ConversationMembers
            join u in db.Users on m.UserId equals u.Id
            where m.ConversationId == evt.ConversationId && m.LeftAt == null && evt.OfflineUserIds.Contains(m.UserId)
                && u.Status == UserStatus.Active && (m.MutedUntil == null || m.MutedUntil <= now)
            select m.UserId).ToListAsync(ct);
        var online = await presence.OnlineAsync(recipients, ct);
        recipients = recipients.Where(id => !online.Contains(id)).ToList();
        if (recipients.Count == 0) return;

        var sessions = await db.Sessions
            .Where(s => recipients.Contains(s.UserId) && s.RevokedAt == null && s.ExpiresAt > now && s.PushToken != null && s.PushPlatform != null)
            .ToListAsync(ct);

        var notification = new PushMessage(
            conversation.Type == ConversationType.Group && !string.IsNullOrEmpty(conversation.Title) ? $"{sender} · {conversation.Title}" : sender ?? "",
            Preview(message.Body), conversation.Id, message.Id, message.Seq);

        var dead = new List<Guid>();
        foreach (var session in sessions)
        {
            var transport = senders.FirstOrDefault(s => s.Platform == session.PushPlatform);
            if (transport is null) continue;
            try
            {
                if (await transport.SendAsync(notification, session.PushToken!, ct) == PushResult.InvalidToken) dead.Add(session.Id);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Push to session {SessionId} failed", session.Id);
            }
        }

        if (dead.Count > 0)
            await db.Sessions.Where(s => dead.Contains(s.Id)).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.PushToken, (string?)null).SetProperty(s => s.PushPlatform, (PushPlatform?)null), ct);
    }

    public static string Preview(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return AttachmentPreview;
        return trimmed.Length <= PreviewLength ? trimmed : trimmed[..PreviewLength].TrimEnd() + "…";
    }
}

using System.Security.Claims;
using Chat.Api.Auth;
using Chat.Api.Conversations;
using Chat.Api.Realtime;
using Chat.Data;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Messaging;

public static class PresenceEndpoints
{
    /// <summary>Who is online and who is typing right now, so a client can reconcile after a reconnect.</summary>
    public static void MapPresence(this IEndpointRouteBuilder app)
    {
        app.MapGet("/conversations/{id:guid}/presence", async (
            Guid id, ClaimsPrincipal me, ConversationReader conversations, PresenceAudience audience,
            PresenceService presence, ChatDbContext db, CancellationToken ct) =>
        {
            var userId = me.UserId();
            if (await conversations.FindAsync(userId, id, ct) is null) return ProblemResults.Problem(AuthError.ConversationNotFound);

            var members = await db.ConversationMembers
                .Where(m => m.ConversationId == id && m.LeftAt == null && m.UserId != userId).Select(m => m.UserId).ToListAsync(ct);
            var visible = await audience.WithoutBlocksAsync(userId, members, ct);
            var online = await presence.OnlineAsync(visible, ct);
            var typing = await presence.TypingAsync(id, visible, ct);
            return Results.Ok(new { online = visible.Where(online.Contains), typing = visible.Where(typing.Contains) });
        }).RequireAuthorization();
    }
}

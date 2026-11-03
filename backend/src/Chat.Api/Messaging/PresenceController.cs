using Chat.Api.Auth;
using Chat.Api.Conversations;
using Chat.Api.Realtime;
using Chat.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Messaging;

[Route("conversations/{id:guid}/presence"), Authorize]
public class PresenceController(
    ConversationReader conversations, PresenceAudience audience, PresenceService presence, ChatDbContext db) : ApiControllerBase
{
    /// <summary>Who is online and who is typing right now, so a client can reconcile after a reconnect.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var userId = UserId;
        if (await conversations.FindAsync(userId, id, ct) is null) return Problem(AuthError.ConversationNotFound);

        var members = await db.ConversationMembers
            .Where(m => m.ConversationId == id && m.LeftAt == null && m.UserId != userId).Select(m => m.UserId).ToListAsync(ct);
        var visible = await audience.WithoutBlocksAsync(userId, members, ct);
        var online = await presence.OnlineAsync(visible, ct);
        var typing = await presence.TypingAsync(id, visible, ct);
        return Ok(new { online = visible.Where(online.Contains), typing = visible.Where(typing.Contains) });
    }
}

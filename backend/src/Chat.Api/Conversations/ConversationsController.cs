using Chat.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chat.Api.Conversations;

[Route("conversations"), Authorize]
public class ConversationsController(ConversationReader reader) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(int? limit, DateTimeOffset? before, Guid? beforeId, CancellationToken ct)
    {
        var errors = ConversationRules.ValidatePage(limit);
        if (beforeId is not null && before is null) errors["beforeId"] = ["Send before with beforeId."];
        if (errors.Count > 0) return Invalid(errors);

        return Ok(await reader.ListAsync(UserId, limit ?? ConversationRules.DefaultLimit, before, beforeId, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => OkOr(await reader.DetailAsync(UserId, id, ct));

    [HttpPost("direct")]
    public async Task<IActionResult> OpenDirect(CreateDirectRequest request, [FromServices] ConversationService svc, CancellationToken ct)
    {
        var errors = ConversationRules.Validate(request);
        if (errors.Count > 0) return Invalid(errors);

        var result = await svc.OpenDirectAsync(UserId, request.UserId!.Value, ct);
        if (result.Error is { } error) return Problem(error);
        var (detail, created) = result.Value;
        return created ? Created($"/conversations/{detail.Conversation.Id}", detail) : Ok(detail);
    }

    [HttpPost("group")]
    public async Task<IActionResult> CreateGroup(CreateGroupRequest request, [FromServices] ConversationService svc, CancellationToken ct)
    {
        var errors = ConversationRules.Validate(request);
        if (errors.Count > 0) return Invalid(errors);

        var result = await svc.CreateGroupAsync(UserId, request, ct);
        return result.Error is { } error
            ? Problem(error)
            : Created($"/conversations/{result.Value!.Conversation.Id}", result.Value);
    }

    [HttpPut("{id:guid}/mute")]
    public async Task<IActionResult> Mute(
        Guid id, MuteRequest request, [FromServices] ConversationService svc, [FromServices] TimeProvider time, CancellationToken ct)
    {
        var errors = ConversationRules.Validate(request, time.GetUtcNow());
        if (errors.Count > 0) return Invalid(errors);

        return NoContentOr(await svc.MuteAsync(UserId, id, request.Until, ct));
    }

    [HttpDelete("{id:guid}/mute")]
    public async Task<IActionResult> Unmute(Guid id, [FromServices] ConversationService svc, CancellationToken ct) =>
        NoContentOr(await svc.MuteAsync(UserId, id, null, ct));

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Rename(Guid id, RenameRequest request, [FromServices] GroupService svc, CancellationToken ct)
    {
        var errors = ConversationRules.Validate(request);
        if (errors.Count > 0) return Invalid(errors);

        return await Changed(await svc.RenameAsync(UserId, id, request.Title!, ct), id, ct);
    }

    [HttpPost("{id:guid}/members")]
    public async Task<IActionResult> AddMembers(Guid id, AddMembersRequest request, [FromServices] GroupService svc, CancellationToken ct)
    {
        var errors = ConversationRules.Validate(request);
        if (errors.Count > 0) return Invalid(errors);

        return await Changed(await svc.AddMembersAsync(UserId, id, request.UserIds!, ct), id, ct);
    }

    [HttpDelete("{id:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId, [FromServices] GroupService svc, CancellationToken ct) =>
        await Changed(await svc.RemoveMemberAsync(UserId, id, userId, ct), id, ct);

    [HttpPut("{id:guid}/members/{userId:guid}/role")]
    public async Task<IActionResult> SetRole(
        Guid id, Guid userId, SetRoleRequest request, [FromServices] GroupService svc, CancellationToken ct)
    {
        var errors = ConversationRules.Validate(request);
        if (errors.Count > 0) return Invalid(errors);

        var role = ConversationRules.ParseRole(request.Role)!.Value;
        return await Changed(await svc.SetRoleAsync(UserId, id, userId, role, ct), id, ct);
    }

    [HttpPost("{id:guid}/leave")]
    public async Task<IActionResult> Leave(Guid id, [FromServices] GroupService svc, CancellationToken ct) =>
        NoContentOr(await svc.LeaveAsync(UserId, id, ct));

    /// <summary>After a change to a group: the problem if it failed, else the group as it now stands.</summary>
    private async Task<IActionResult> Changed(AuthError? error, Guid id, CancellationToken ct) =>
        error is not null ? Problem(error) : OkOr(await reader.DetailAsync(UserId, id, ct));
}

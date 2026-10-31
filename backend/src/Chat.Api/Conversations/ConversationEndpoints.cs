using System.Security.Claims;
using Chat.Api.Auth;

namespace Chat.Api.Conversations;

public static class ConversationEndpoints
{
    public static void MapConversations(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/conversations").RequireAuthorization();

        group.MapGet("/", async (int? limit, DateTimeOffset? before, ClaimsPrincipal me, ConversationReader reader, CancellationToken ct) =>
        {
            var errors = ConversationRules.ValidatePage(limit);
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            return Results.Ok(await reader.ListAsync(me.UserId(), limit ?? ConversationRules.DefaultLimit, before, ct));
        });

        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal me, ConversationReader reader, CancellationToken ct) =>
            Respond(await reader.DetailAsync(me.UserId(), id, ct)));

        group.MapPost("/direct", async (CreateDirectRequest request, ClaimsPrincipal me, ConversationService svc, CancellationToken ct) =>
        {
            var errors = ConversationRules.Validate(request);
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            var result = await svc.OpenDirectAsync(me.UserId(), request.UserId!.Value, ct);
            if (result.Error is { } error) return ProblemResults.Problem(error);
            var (detail, created) = result.Value;
            return created ? Results.Created($"/conversations/{detail.Conversation.Id}", detail) : Results.Ok(detail);
        });

        group.MapPost("/group", async (CreateGroupRequest request, ClaimsPrincipal me, ConversationService svc, CancellationToken ct) =>
        {
            var errors = ConversationRules.Validate(request);
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            var result = await svc.CreateGroupAsync(me.UserId(), request, ct);
            return result.Error is { } error
                ? ProblemResults.Problem(error)
                : Results.Created($"/conversations/{result.Value!.Conversation.Id}", result.Value);
        });

        group.MapPut("/{id:guid}/mute", async (
            Guid id, MuteRequest request, ClaimsPrincipal me, ConversationService svc, TimeProvider time, CancellationToken ct) =>
        {
            var errors = ConversationRules.Validate(request, time.GetUtcNow());
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            return NoContent(await svc.MuteAsync(me.UserId(), id, request.Until, ct));
        });

        group.MapDelete("/{id:guid}/mute", async (Guid id, ClaimsPrincipal me, ConversationService svc, CancellationToken ct) =>
            NoContent(await svc.MuteAsync(me.UserId(), id, null, ct)));

        group.MapPatch("/{id:guid}", async (
            Guid id, RenameRequest request, ClaimsPrincipal me, GroupService svc, ConversationReader reader, CancellationToken ct) =>
        {
            var errors = ConversationRules.Validate(request);
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            return await Changed(await svc.RenameAsync(me.UserId(), id, request.Title!, ct), me, id, reader, ct);
        });

        group.MapPost("/{id:guid}/members", async (
            Guid id, AddMembersRequest request, ClaimsPrincipal me, GroupService svc, ConversationReader reader, CancellationToken ct) =>
        {
            var errors = ConversationRules.Validate(request);
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            return await Changed(await svc.AddMembersAsync(me.UserId(), id, request.UserIds!, ct), me, id, reader, ct);
        });

        group.MapDelete("/{id:guid}/members/{userId:guid}", async (
            Guid id, Guid userId, ClaimsPrincipal me, GroupService svc, ConversationReader reader, CancellationToken ct) =>
            await Changed(await svc.RemoveMemberAsync(me.UserId(), id, userId, ct), me, id, reader, ct));

        group.MapPut("/{id:guid}/members/{userId:guid}/role", async (
            Guid id, Guid userId, SetRoleRequest request, ClaimsPrincipal me, GroupService svc, ConversationReader reader, CancellationToken ct) =>
        {
            var errors = ConversationRules.Validate(request);
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            var role = ConversationRules.ParseRole(request.Role)!.Value;
            return await Changed(await svc.SetRoleAsync(me.UserId(), id, userId, role, ct), me, id, reader, ct);
        });

        group.MapPost("/{id:guid}/leave", async (Guid id, ClaimsPrincipal me, GroupService svc, CancellationToken ct) =>
            NoContent(await svc.LeaveAsync(me.UserId(), id, ct)));
    }

    private static IResult Respond(Result<ConversationDetail> result) =>
        result.Error is { } error ? ProblemResults.Problem(error) : Results.Ok(result.Value);

    private static IResult NoContent(AuthError? error) => error is null ? Results.NoContent() : ProblemResults.Problem(error);

    /// <summary>After a change to a group: the problem if it failed, else the group as it now stands.</summary>
    private static async Task<IResult> Changed(
        AuthError? error, ClaimsPrincipal me, Guid id, ConversationReader reader, CancellationToken ct) =>
        error is not null ? ProblemResults.Problem(error) : Respond(await reader.DetailAsync(me.UserId(), id, ct));
}

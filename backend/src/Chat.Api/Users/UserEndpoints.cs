using System.Security.Claims;
using Chat.Api.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Chat.Api.Users;

public static class UserEndpoints
{
    public static void MapUsers(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/users").RequireAuthorization();

        users.MapPatch("/me", async (UpdateProfileRequest request, ClaimsPrincipal me, UserService svc, CancellationToken ct) =>
        {
            var errors = UserValidation.Validate(request);
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            var summary = await svc.UpdateProfileAsync(me.UserId(), request, ct);
            return summary is null ? Results.Unauthorized() : Results.Ok(summary);
        });

        users.MapGet("/search", async (string? q, int? limit, ClaimsPrincipal me, UserService svc, CancellationToken ct) =>
        {
            var errors = UserValidation.ValidateSearch(q, limit);
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            return Results.Ok(await svc.SearchAsync(me.UserId(), q!, limit ?? UserValidation.DefaultLimit, ct));
        });

        users.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal me, UserService svc, CancellationToken ct) =>
        {
            var result = await svc.GetAsync(me.UserId(), id, ct);
            return result.Error is { } error ? ProblemResults.Problem(error) : Results.Ok(result.Value);
        });

        var contacts = app.MapGroup("/contacts").RequireAuthorization();

        contacts.MapGet("/", async (ClaimsPrincipal me, ContactService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListContactsAsync(me.UserId(), ct)));

        contacts.MapPut("/{id:guid}", async (
            Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ContactRequest? request,
            ClaimsPrincipal me, ContactService svc, CancellationToken ct) =>
        {
            var errors = UserValidation.Validate(request);
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            var result = await svc.SaveContactAsync(me.UserId(), id, request, ct);
            return result.Error is { } error ? ProblemResults.Problem(error) : Results.Ok(result.Value);
        });

        contacts.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal me, ContactService svc, CancellationToken ct) =>
        {
            await svc.RemoveContactAsync(me.UserId(), id, ct);
            return Results.NoContent();
        });

        var blocks = app.MapGroup("/blocks").RequireAuthorization();

        blocks.MapGet("/", async (ClaimsPrincipal me, ContactService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListBlocksAsync(me.UserId(), ct)));

        blocks.MapPut("/{id:guid}", async (Guid id, ClaimsPrincipal me, ContactService svc, CancellationToken ct) =>
            await svc.BlockAsync(me.UserId(), id, ct) is { } error ? ProblemResults.Problem(error) : Results.NoContent());

        blocks.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal me, ContactService svc, CancellationToken ct) =>
        {
            await svc.UnblockAsync(me.UserId(), id, ct);
            return Results.NoContent();
        });
    }
}

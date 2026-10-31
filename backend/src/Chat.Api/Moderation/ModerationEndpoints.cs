using System.Security.Claims;
using Chat.Api.Auth;
using Chat.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chat.Api.Moderation;

public static class ModerationEndpoints
{
    public static void MapModeration(this IEndpointRouteBuilder app)
    {
        app.MapPost("/reports", async (SubmitReportRequest request, ClaimsPrincipal me, ReportService svc, CancellationToken ct) =>
        {
            var errors = ReportService.Validate(request, svc.MaxDetails);
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            var result = await svc.SubmitAsync(me.UserId(), request, ct);
            if (result.Error is { } error) return ProblemResults.Problem(error);
            return result.Value!.Created
                ? Results.Created($"/reports/{result.Value.Report.Id}", result.Value.Report)
                : Results.Ok(result.Value.Report);
        }).RequireAuthorization();

        // The JWT policy is the first gate; the filter re-reads the role, since a demoted admin's token stays valid for minutes.
        var admin = app.MapGroup("/admin").RequireAuthorization(Policies.Admin).AddEndpointFilter(async (context, next) =>
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<ChatDbContext>();
            var id = context.HttpContext.User.UserId();
            var isAdmin = await db.Users.AnyAsync(u => u.Id == id && u.Role == UserRole.Admin && u.Status == UserStatus.Active, context.HttpContext.RequestAborted);
            return isAdmin ? await next(context) : ProblemResults.Problem(AuthError.AdminOnly);
        });

        admin.MapGet("/reports", async (string? status, int? limit, Guid? after, ModerationService svc, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            ReportStatus? filter = (status ?? "open").ToLowerInvariant() switch
            {
                "open" => ReportStatus.Open, "actioned" => ReportStatus.Actioned, "dismissed" => ReportStatus.Dismissed, _ => null,
            };
            if (filter is null && !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase)) errors["status"] = ["Use open, actioned, dismissed or all."];
            if (limit is < 1 or > ModerationService.MaxReportLimit) errors["limit"] = [$"Use 1-{ModerationService.MaxReportLimit}."];
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            return Results.Ok(await svc.ListAsync(filter, limit ?? ModerationService.DefaultReportLimit, after, ct));
        });

        admin.MapGet("/reports/{id:guid}", async (Guid id, ClaimsPrincipal me, ModerationService svc, CancellationToken ct) =>
        {
            var result = await svc.GetAsync(me.UserId(), id, ct);
            return result.Error is { } error ? ProblemResults.Problem(error) : Results.Ok(result.Value);
        });

        admin.MapPost("/reports/{id:guid}/resolve", async (Guid id, ResolveRequest request, ClaimsPrincipal me, ModerationService svc, CancellationToken ct) =>
        {
            var decision = request.Decision?.ToLowerInvariant();
            if (decision is not ("action" or "dismiss"))
                return ProblemResults.Invalid(new() { ["decision"] = ["Use action or dismiss."] });
            if (request.Ban == true && decision != "action")
                return ProblemResults.Invalid(new() { ["ban"] = ["Only an actioned report can ban."] });

            return NoContent(await svc.ResolveAsync(me.UserId(), id, decision == "action", request.Ban == true, ct));
        });

        admin.MapPost("/users/{id:guid}/ban", async (
            Guid id, BanRequest? request, ClaimsPrincipal me, ModerationService svc, CancellationToken ct) =>
        {
            if (request?.Reason is { Length: > 500 }) return ProblemResults.Invalid(new() { ["reason"] = ["At most 500 characters."] });
            return NoContent(await svc.BanAsync(me.UserId(), id, request?.Reason, ct));
        });

        admin.MapPost("/users/{id:guid}/unban", async (Guid id, ClaimsPrincipal me, ModerationService svc, CancellationToken ct) =>
            NoContent(await svc.UnbanAsync(me.UserId(), id, ct)));

        admin.MapGet("/audit", async (
            Guid? actorId, string? action, DateTimeOffset? from, DateTimeOffset? to, int? limit, long? before,
            ModerationService svc, CancellationToken ct) =>
        {
            if (limit is < 1 or > ModerationService.MaxAuditLimit)
                return ProblemResults.Invalid(new() { ["limit"] = [$"Use 1-{ModerationService.MaxAuditLimit}."] });
            return Results.Ok(await svc.QueryAuditAsync(actorId, action, from, to, limit ?? ModerationService.DefaultAuditLimit, before, ct));
        });
    }

    private static IResult NoContent(AuthError? error) => error is null ? Results.NoContent() : ProblemResults.Problem(error);
}

using Chat.Api.Auth;
using Chat.Api.Files;
using Chat.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Moderation;

/// <remarks>
/// The JWT policy is the first gate; <see cref="ActiveAdminFilter"/> re-reads the role, since a demoted admin's token stays valid
/// for minutes.
/// </remarks>
[Route("admin"), Authorize(Policy = Policies.Admin), TypeFilter<ActiveAdminFilter>]
public class AdminController(ModerationService moderation) : ApiControllerBase
{
    [HttpGet("reports")]
    public async Task<IActionResult> Reports(string? status, int? limit, Guid? after, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        ReportStatus? filter = (status ?? "open").ToLowerInvariant() switch
        {
            "open" => ReportStatus.Open, "actioned" => ReportStatus.Actioned, "dismissed" => ReportStatus.Dismissed, _ => null,
        };
        if (filter is null && !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase)) errors["status"] = ["Use open, actioned, dismissed or all."];
        if (limit is < 1 or > ModerationService.MaxReportLimit) errors["limit"] = [$"Use 1-{ModerationService.MaxReportLimit}."];
        if (errors.Count > 0) return Invalid(errors);

        return Ok(await moderation.ListAsync(filter, limit ?? ModerationService.DefaultReportLimit, after, ct));
    }

    [HttpGet("reports/{id:guid}")]
    public async Task<IActionResult> Report(Guid id, CancellationToken ct) => OkOr(await moderation.GetAsync(UserId, id, ct));

    [HttpGet("reports/{id:guid}/media/{mediaId:guid}")]
    public async Task<IActionResult> Evidence(Guid id, Guid mediaId, [FromServices] MediaService media, CancellationToken ct)
    {
        if (await moderation.CheckEvidenceFileAsync(UserId, id, mediaId, ct) is { } error) return Problem(error);
        var file = await media.OpenAllowedAsync(mediaId, ct);
        return file.Error is { } missing ? Problem(missing) : MediaResponse.Serve(HttpContext, file.Value!);
    }

    [HttpPost("reports/{id:guid}/resolve")]
    public async Task<IActionResult> Resolve(Guid id, ResolveRequest request, CancellationToken ct)
    {
        var decision = request.Decision?.ToLowerInvariant();
        if (decision is not ("action" or "dismiss"))
            return Invalid(new() { ["decision"] = ["Use action or dismiss."] });
        if (request.Ban == true && decision != "action")
            return Invalid(new() { ["ban"] = ["Only an actioned report can ban."] });

        return NoContentOr(await moderation.ResolveAsync(UserId, id, decision == "action", request.Ban == true, ct));
    }

    [HttpPost("users/{id:guid}/ban")]
    public async Task<IActionResult> Ban(
        Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] BanRequest? request, CancellationToken ct)
    {
        if (request?.Reason is { Length: > 500 }) return Invalid(new() { ["reason"] = ["At most 500 characters."] });
        return NoContentOr(await moderation.BanAsync(UserId, id, request?.Reason, ct));
    }

    [HttpPost("users/{id:guid}/unban")]
    public async Task<IActionResult> Unban(Guid id, CancellationToken ct) => NoContentOr(await moderation.UnbanAsync(UserId, id, ct));

    [HttpGet("audit")]
    public async Task<IActionResult> Audit(
        Guid? actorId, [FromQuery] string? action, DateTimeOffset? from, DateTimeOffset? to, int? limit, long? before, CancellationToken ct)
    {
        if (limit is < 1 or > ModerationService.MaxAuditLimit)
            return Invalid(new() { ["limit"] = [$"Use 1-{ModerationService.MaxAuditLimit}."] });
        return Ok(await moderation.QueryAuditAsync(actorId, action, from, to, limit ?? ModerationService.DefaultAuditLimit, before, ct));
    }
}

/// <summary>Lets the request through only if the caller is, in the database, an active admin right now.</summary>
public sealed class ActiveAdminFilter(ChatDbContext db) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        var id = http.User.UserId();
        var isAdmin = await db.Users.AnyAsync(u => u.Id == id && u.Role == UserRole.Admin && u.Status == UserStatus.Active, http.RequestAborted);
        if (!isAdmin) context.Result = ProblemResults.Problem(http, AuthError.AdminOnly);
    }
}

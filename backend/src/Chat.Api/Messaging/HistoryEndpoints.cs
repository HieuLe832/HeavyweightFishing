using System.Security.Claims;
using Chat.Api.Auth;

namespace Chat.Api.Messaging;

public static class HistoryEndpoints
{
    public static void MapHistory(this IEndpointRouteBuilder app)
    {
        app.MapGet("/conversations/{id:guid}/messages", async (
            Guid id, long? before, long? after, int? limit, ClaimsPrincipal me, MessageReader reader, CancellationToken ct) =>
        {
            var errors = Validate(before, after, limit);
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            var result = await reader.PageAsync(me.UserId(), id, before, after, limit ?? MessageReader.DefaultLimit, ct);
            return result.Error is { } error ? ProblemResults.Problem(error) : Results.Ok(result.Value);
        }).RequireAuthorization();
    }

    private static Dictionary<string, string[]> Validate(long? before, long? after, int? limit)
    {
        var errors = new Dictionary<string, string[]>();
        if (limit is < 1 or > MessageReader.MaxLimit) errors["limit"] = [$"Use a limit from 1 to {MessageReader.MaxLimit}."];
        if (before is < 0) errors["before"] = ["A seq cannot be negative."];
        if (after is < 0) errors["after"] = ["A seq cannot be negative."];
        if (before is not null && after is not null) errors["after"] = ["Use either before or after, not both."];
        return errors;
    }
}

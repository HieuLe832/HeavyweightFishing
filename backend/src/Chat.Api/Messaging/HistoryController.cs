using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chat.Api.Messaging;

[Route("conversations/{id:guid}/messages"), Authorize]
public class HistoryController(MessageReader reader) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Page(Guid id, long? before, long? after, int? limit, CancellationToken ct)
    {
        var errors = Validate(before, after, limit);
        if (errors.Count > 0) return Invalid(errors);

        return OkOr(await reader.PageAsync(UserId, id, before, after, limit ?? MessageReader.DefaultLimit, ct));
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

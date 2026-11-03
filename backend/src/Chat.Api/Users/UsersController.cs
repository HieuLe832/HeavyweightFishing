using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chat.Api.Users;

[Route("users"), Authorize]
public class UsersController(UserService users) : ApiControllerBase
{
    [HttpPatch("me")]
    public async Task<IActionResult> UpdateProfile(UpdateProfileRequest request, CancellationToken ct)
    {
        var errors = UserValidation.Validate(request);
        if (errors.Count > 0) return Invalid(errors);

        var summary = await users.UpdateProfileAsync(UserId, request, ct);
        return summary is null ? Unauthorized() : Ok(summary);
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search(string? q, int? limit, CancellationToken ct)
    {
        var errors = UserValidation.ValidateSearch(q, limit);
        if (errors.Count > 0) return Invalid(errors);

        return Ok(await users.SearchAsync(UserId, q!, limit ?? UserValidation.DefaultLimit, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => OkOr(await users.GetAsync(UserId, id, ct));
}

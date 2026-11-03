using Chat.Api.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Chat.Api;

/// <summary>
/// The base for every API controller: attribute routes, JSON bodies, and the error shape the services report through
/// <see cref="AuthError"/> (ProblemDetails with a <c>code</c>).
/// </summary>
[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>The signed-in user. Only on an action that requires authorization.</summary>
    protected Guid UserId => User.UserId();

    protected ObjectResult Problem(AuthError error) => ProblemResults.Problem(HttpContext, error);

    protected ObjectResult Invalid(Dictionary<string, string[]> errors) => ProblemResults.Invalid(HttpContext, errors);

    protected IActionResult NoContentOr(AuthError? error) => error is null ? NoContent() : Problem(error);

    protected IActionResult OkOr<T>(Result<T> result) => result.Error is { } error ? Problem(error) : Ok(result.Value);
}

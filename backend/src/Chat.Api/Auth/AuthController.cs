using Chat.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Auth;

/// <remarks>
/// The actions that need no session share a per-address limit (<see cref="AuthRateLimit"/>); refresh (a 256-bit secret) and the
/// signed-in ones do not.
/// </remarks>
[Route("auth")]
public class AuthController(AccountService accounts, SignInService signIn) : ApiControllerBase
{
    [HttpPost("register"), EnableRateLimiting(AuthRateLimit.Policy)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        var errors = AuthValidation.Validate(request);
        if (errors.Count > 0) return Invalid(errors);

        var result = await accounts.RegisterAsync(request, ct);
        return result.Error is { } error ? Problem(error) : StatusCode(StatusCodes.Status201Created, new { userId = result.Value });
    }

    [HttpPost("verify-email"), EnableRateLimiting(AuthRateLimit.Policy)]
    public async Task<IActionResult> VerifyEmail(VerifyEmailRequest request, CancellationToken ct)
    {
        var errors = AuthValidation.Validate(request);
        if (errors.Count > 0) return Invalid(errors);

        var result = await accounts.VerifyEmailAsync(request, ct);
        return result.Error is { } error ? Problem(error) : NoContent();
    }

    [HttpPost("resend-verification"), EnableRateLimiting(AuthRateLimit.Policy)]
    public async Task<IActionResult> ResendVerification(EmailRequest request, CancellationToken ct)
    {
        var errors = AuthValidation.Validate(request);
        if (errors.Count > 0) return Invalid(errors);

        await accounts.ResendVerificationAsync(request.Email!, ct);
        return StatusCode(StatusCodes.Status202Accepted);
    }

    [HttpPost("password-reset/request"), EnableRateLimiting(AuthRateLimit.Policy)]
    public async Task<IActionResult> RequestPasswordReset(EmailRequest request, CancellationToken ct)
    {
        var errors = AuthValidation.Validate(request);
        if (errors.Count > 0) return Invalid(errors);

        await accounts.RequestPasswordResetAsync(request.Email!, ct);
        return StatusCode(StatusCodes.Status202Accepted);
    }

    [HttpPost("password-reset/confirm"), EnableRateLimiting(AuthRateLimit.Policy)]
    public async Task<IActionResult> ConfirmPasswordReset(PasswordResetConfirmRequest request, CancellationToken ct)
    {
        var errors = AuthValidation.Validate(request);
        if (errors.Count > 0) return Invalid(errors);

        var result = await accounts.ConfirmPasswordResetAsync(request, ct);
        return result.Error is { } error ? Problem(error) : NoContent();
    }

    [HttpPost("login"), EnableRateLimiting(AuthRateLimit.Policy)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var errors = AuthValidation.Validate(request);
        if (errors.Count > 0) return Invalid(errors);

        var client = new ClientInfo(
            Request.Headers.UserAgent.ToString(),
            HttpContext.Connection.RemoteIpAddress ?? System.Net.IPAddress.Loopback);
        return OkOr(await signIn.LoginAsync(request, client, ct));
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken ct)
    {
        var errors = AuthValidation.Validate(request);
        if (errors.Count > 0) return Invalid(errors);

        return OkOr(await signIn.RefreshAsync(request.RefreshToken!, ct));
    }

    [HttpPost("logout"), Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await signIn.LogoutAsync(User.SessionId(), ct);
        return NoContent();
    }

    [HttpGet("sessions"), Authorize]
    public async Task<IActionResult> Sessions(CancellationToken ct) =>
        Ok(await signIn.ListSessionsAsync(UserId, User.SessionId(), ct));

    [HttpGet("me"), Authorize]
    public async Task<IActionResult> Me([FromServices] ChatDbContext db, CancellationToken ct)
    {
        var id = UserId;
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
        return user is null ? Unauthorized() : Ok(SignInService.Summary(user));
    }
}

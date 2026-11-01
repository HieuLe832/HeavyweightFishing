using System.Security.Claims;
using Chat.Data;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Auth;

public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/auth");
        // The endpoints that need no session share a per-address limit; refresh (a 256-bit secret) and the signed-in ones do not.
        var anonymous = auth.MapGroup("").RequireRateLimiting(AuthRateLimit.Policy);

        anonymous.MapPost("/register", async (RegisterRequest request, AccountService accounts, CancellationToken ct) =>
        {
            var errors = AuthValidation.Validate(request);
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            var result = await accounts.RegisterAsync(request, ct);
            return result.Error is { } error
                ? ProblemResults.Problem(error)
                : Results.Created((string?)null, new { userId = result.Value });
        });

        anonymous.MapPost("/verify-email", async (VerifyEmailRequest request, AccountService accounts, CancellationToken ct) =>
        {
            var errors = AuthValidation.Validate(request);
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            var result = await accounts.VerifyEmailAsync(request, ct);
            return result.Error is { } error ? ProblemResults.Problem(error) : Results.NoContent();
        });

        anonymous.MapPost("/resend-verification", async (EmailRequest request, AccountService accounts, CancellationToken ct) =>
        {
            var errors = AuthValidation.Validate(request);
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            await accounts.ResendVerificationAsync(request.Email!, ct);
            return Results.Accepted();
        });

        anonymous.MapPost("/password-reset/request", async (EmailRequest request, AccountService accounts, CancellationToken ct) =>
        {
            var errors = AuthValidation.Validate(request);
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            await accounts.RequestPasswordResetAsync(request.Email!, ct);
            return Results.Accepted();
        });

        anonymous.MapPost("/password-reset/confirm", async (
            PasswordResetConfirmRequest request, AccountService accounts, CancellationToken ct) =>
        {
            var errors = AuthValidation.Validate(request);
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            var result = await accounts.ConfirmPasswordResetAsync(request, ct);
            return result.Error is { } error ? ProblemResults.Problem(error) : Results.NoContent();
        });

        anonymous.MapPost("/login", async (
            LoginRequest request, HttpContext http, SignInService signIn, CancellationToken ct) =>
        {
            var errors = AuthValidation.Validate(request);
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            var client = new ClientInfo(
                http.Request.Headers.UserAgent.ToString(),
                http.Connection.RemoteIpAddress ?? System.Net.IPAddress.Loopback);
            var result = await signIn.LoginAsync(request, client, ct);
            return result.Error is { } error ? ProblemResults.Problem(error) : Results.Ok(result.Value);
        });

        auth.MapPost("/refresh", async (RefreshRequest request, SignInService signIn, CancellationToken ct) =>
        {
            var errors = AuthValidation.Validate(request);
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            var result = await signIn.RefreshAsync(request.RefreshToken!, ct);
            return result.Error is { } error ? ProblemResults.Problem(error) : Results.Ok(result.Value);
        });

        auth.MapPost("/logout", async (ClaimsPrincipal principal, SignInService signIn, CancellationToken ct) =>
        {
            await signIn.LogoutAsync(principal.SessionId(), ct);
            return Results.NoContent();
        }).RequireAuthorization();

        auth.MapGet("/sessions", async (ClaimsPrincipal principal, SignInService signIn, CancellationToken ct) =>
            Results.Ok(await signIn.ListSessionsAsync(principal.UserId(), principal.SessionId(), ct))
        ).RequireAuthorization();

        auth.MapGet("/me", async (ClaimsPrincipal principal, ChatDbContext db, CancellationToken ct) =>
        {
            var id = principal.UserId();
            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
            return user is null ? Results.Unauthorized() : Results.Ok(SignInService.Summary(user));
        }).RequireAuthorization();
    }
}

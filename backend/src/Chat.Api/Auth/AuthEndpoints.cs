using System.Security.Claims;
using Chat.Data;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Auth;

public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/auth");

        auth.MapPost("/register", async (RegisterRequest request, AccountService accounts, CancellationToken ct) =>
        {
            var errors = AuthValidation.Validate(request);
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            var result = await accounts.RegisterAsync(request, ct);
            return result.Error is { } error
                ? ProblemResults.Problem(error)
                : Results.Created((string?)null, new { userId = result.Value });
        });

        auth.MapPost("/verify-email", async (VerifyEmailRequest request, AccountService accounts, CancellationToken ct) =>
        {
            var errors = AuthValidation.Validate(request);
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            var result = await accounts.VerifyEmailAsync(request, ct);
            return result.Error is { } error ? ProblemResults.Problem(error) : Results.NoContent();
        });

        auth.MapPost("/resend-verification", async (EmailRequest request, AccountService accounts, CancellationToken ct) =>
        {
            var errors = AuthValidation.Validate(request);
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            await accounts.ResendVerificationAsync(request.Email!, ct);
            return Results.Accepted();
        });

        auth.MapPost("/login", async (
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

        auth.MapGet("/me", async (ClaimsPrincipal principal, ChatDbContext db, CancellationToken ct) =>
        {
            var id = principal.UserId();
            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
            return user is null ? Results.Unauthorized() : Results.Ok(SignInService.Summary(user));
        }).RequireAuthorization();
    }
}

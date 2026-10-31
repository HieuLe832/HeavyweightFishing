using Chat.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Chat.Api.Auth;

/// <summary>
/// Process 1.7: validates the JWT, then checks its session in D2. The extra lookup is what makes logout, password
/// reset and bans take effect immediately instead of when the access token runs out.
/// </summary>
public class JwtBearerSetup(IOptions<AuthOptions> authOptions, TimeProvider time) : IConfigureNamedOptions<JwtBearerOptions>
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    public void Configure(JwtBearerOptions options) => Configure(JwtBearerDefaults.AuthenticationScheme, options);

    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme) return;
        var o = authOptions.Value;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = o.Issuer,
            ValidAudience = o.Audience,
            IssuerSigningKey = TokenService.SigningKey(o),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            NameClaimType = AuthClaims.Subject,
            RoleClaimType = AuthClaims.Role,
            ClockSkew = ClockSkew,
            // The default validator reads the system clock; ours follows TimeProvider so tests can move time.
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                var now = time.GetUtcNow().UtcDateTime;
                return expires is not null && now <= expires.Value + ClockSkew
                       && (notBefore is null || now >= notBefore.Value - ClockSkew);
            },
        };
        options.Events = new JwtBearerEvents { OnMessageReceived = TokenFromQuery, OnTokenValidated = CheckSessionAsync };
    }

    /// <summary>Browsers cannot set headers on a WebSocket, so the hub takes the token from the query string.</summary>
    private static Task TokenFromQuery(MessageReceivedContext context)
    {
        if (context.HttpContext.Request.Path.StartsWithSegments("/hubs/chat")
            && context.Request.Query["access_token"] is { Count: > 0 } token)
            context.Token = token;
        return Task.CompletedTask;
    }

    private async Task CheckSessionAsync(TokenValidatedContext context)
    {
        var principal = context.Principal!;
        if (!Guid.TryParse(principal.FindFirst(AuthClaims.Subject)?.Value, out var userId)
            || !Guid.TryParse(principal.FindFirst(AuthClaims.Session)?.Value, out var sessionId))
        {
            context.Fail("Token has no user or session.");
            return;
        }

        var now = time.GetUtcNow();
        var db = context.HttpContext.RequestServices.GetRequiredService<ChatDbContext>();
        var live = await db.Sessions.AnyAsync(s =>
            s.Id == sessionId && s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now,
            context.HttpContext.RequestAborted);
        if (!live) context.Fail("Session is revoked or expired.");
    }
}

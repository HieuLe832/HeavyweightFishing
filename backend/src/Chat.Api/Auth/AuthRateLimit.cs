using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Chat.Api.Auth;

/// <summary>
/// A per-address limit on the endpoints that work without a session (register, verify, resend, password reset, login). The account
/// lockout and the code attempt limit are per account; this one stops a single address from spraying many accounts. Behind a proxy
/// it needs the client's address (see <c>ForwardedHeaders</c> in Program), or every user shares one bucket.
/// </summary>
public static class AuthRateLimit
{
    public const string Policy = "auth-anonymous";

    public static IServiceCollection AddAuthRateLimit(this IServiceCollection services) => services.AddRateLimiter(o =>
    {
        o.AddPolicy(Policy, http =>
        {
            var auth = http.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;
            return RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = auth.IpLimitCount,
                Window = TimeSpan.FromSeconds(auth.IpLimitWindowSeconds),
            });
        });
        o.OnRejected = (context, _) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
            return new ValueTask(Results.Problem(ProblemResults.Details(context.HttpContext, AuthError.RateLimited)).ExecuteAsync(context.HttpContext));
        };
    });
}

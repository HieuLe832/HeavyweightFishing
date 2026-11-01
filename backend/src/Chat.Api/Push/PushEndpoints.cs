using System.Security.Claims;
using Chat.Api.Auth;
using Chat.Data;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Push;

public record PushTokenRequest(string? Platform, string? Token);

public static class PushEndpoints
{
    public const int MaxTokenLength = 4096;

    public static void MapPush(this IEndpointRouteBuilder app)
    {
        var tokens = app.MapGroup("/auth/sessions/current/push-token").RequireAuthorization();

        tokens.MapPut("/", async (PushTokenRequest request, ClaimsPrincipal me, ChatDbContext db, CancellationToken ct) =>
        {
            var errors = Validate(request, out var platform);
            if (errors.Count > 0) return ProblemResults.Invalid(errors);

            var userId = me.UserId();
            var token = request.Token!;
            // A token is one physical device, so it leaves every other session: the same person's (no double notifications after a
            // re-login) and anyone else's (a shared device must not show the previous person's messages).
            await db.Sessions.Where(s => s.PushToken == token && s.Id != me.SessionId())
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.PushToken, (string?)null).SetProperty(s => s.PushPlatform, (PushPlatform?)null), ct);
            await db.Sessions.Where(s => s.Id == me.SessionId() && s.UserId == userId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.PushToken, token).SetProperty(s => s.PushPlatform, (PushPlatform?)platform), ct);
            return Results.NoContent();
        });

        tokens.MapDelete("/", async (ClaimsPrincipal me, ChatDbContext db, CancellationToken ct) =>
        {
            await db.Sessions.Where(s => s.Id == me.SessionId() && s.UserId == me.UserId())
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.PushToken, (string?)null).SetProperty(s => s.PushPlatform, (PushPlatform?)null), ct);
            return Results.NoContent();
        });
    }

    private static Dictionary<string, string[]> Validate(PushTokenRequest r, out PushPlatform platform)
    {
        var errors = new Dictionary<string, string[]>();
        PushPlatform? parsed = r.Platform?.ToLowerInvariant() switch
        {
            "fcm" => PushPlatform.Fcm,
            "apns" => PushPlatform.Apns,
            "web" => PushPlatform.Web,
            _ => null,
        };
        platform = parsed ?? default;
        if (parsed is null) errors["platform"] = ["Use fcm, apns or web."];
        if (string.IsNullOrEmpty(r.Token) || r.Token.Length > MaxTokenLength || r.Token.Any(char.IsWhiteSpace))
            errors["token"] = [$"Send a token of 1-{MaxTokenLength} characters without spaces."];
        return errors;
    }
}

using Chat.Api.Auth;
using Chat.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Push;

public record PushTokenRequest(string? Platform, string? Token);

[Route("auth/sessions/current/push-token"), Authorize]
public class PushTokenController(ChatDbContext db) : ApiControllerBase
{
    public const int MaxTokenLength = 4096;

    [HttpPut]
    public async Task<IActionResult> Register(PushTokenRequest request, CancellationToken ct)
    {
        var errors = Validate(request, out var platform);
        if (errors.Count > 0) return Invalid(errors);

        var userId = UserId;
        var sessionId = User.SessionId();
        var token = request.Token!;
        // A token is one physical device, so it leaves every other session: the same person's (no double notifications after a
        // re-login) and anyone else's (a shared device must not show the previous person's messages).
        await db.Sessions.Where(s => s.PushToken == token && s.Id != sessionId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.PushToken, (string?)null).SetProperty(s => s.PushPlatform, (PushPlatform?)null), ct);
        await db.Sessions.Where(s => s.Id == sessionId && s.UserId == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.PushToken, token).SetProperty(s => s.PushPlatform, (PushPlatform?)platform), ct);
        return NoContent();
    }

    [HttpDelete]
    public async Task<IActionResult> Remove(CancellationToken ct)
    {
        var userId = UserId;
        var sessionId = User.SessionId();
        await db.Sessions.Where(s => s.Id == sessionId && s.UserId == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.PushToken, (string?)null).SetProperty(s => s.PushPlatform, (PushPlatform?)null), ct);
        return NoContent();
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

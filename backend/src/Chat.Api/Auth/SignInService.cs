using System.Net;
using Chat.Data;
using Chat.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chat.Api.Auth;

public static class RevokeReasons
{
    public const string Logout = "logout";
    public const string RefreshReuse = "refresh_reuse";
    public const string UserInactive = "user_inactive";
    public const string PasswordReset = "password_reset";
    public const string Banned = "banned";
}

public record ClientInfo(string UserAgent, IPAddress IpAddress);

/// <summary>Processes 1.4 Sign In with Password and 1.5 Manage Sessions &amp; Tokens.</summary>
public class SignInService(
    ChatDbContext db,
    IPasswordHasher<User> hasher,
    TokenService tokens,
    TimeProvider time,
    IOptions<AuthOptions> options)
{
    private const string DefaultDeviceName = "Unknown device";
    private static readonly User DummyUser = new() { Username = "", Email = "", DisplayName = "" };
    private static readonly string DummyHash = new PasswordHasher<User>().HashPassword(DummyUser, "dummy-password");

    private readonly AuthOptions _o = options.Value;

    public async Task<Result<AuthResponse>> LoginAsync(LoginRequest request, ClientInfo client, CancellationToken ct)
    {
        var identifier = request.Identifier!.Trim();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == identifier || u.Username == identifier, ct);

        // Unknown users and passwordless accounts still pay for one hash check, so timing does not tell them apart.
        if (user?.PasswordHash is null)
        {
            hasher.VerifyHashedPassword(DummyUser, DummyHash, request.Password!);
            return AuthError.InvalidCredentials;
        }

        var now = time.GetUtcNow();
        var passwordOk = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password!)
            != PasswordVerificationResult.Failed;

        // While locked, the answer is the same whatever the password, so a guesser cannot tell a right guess.
        if (user.LockedUntil > now) return AuthError.InvalidCredentials;

        if (!passwordOk)
        {
            await RegisterFailureAsync(user, now, ct);
            return AuthError.InvalidCredentials;
        }

        if (user.FailedLoginCount != 0 || user.LockedUntil is not null)
            await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.FailedLoginCount, (short)0)
                .SetProperty(u => u.LockedUntil, (DateTimeOffset?)null), ct);

        if (user.Status == UserStatus.Pending) return AuthError.EmailNotVerified;
        if (user.Status == UserStatus.Banned) return AuthError.AccountBanned;

        return await StartSessionAsync(user, request.DeviceName, client, now, ct);
    }

    public async Task<Result<AuthResponse>> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var hash = TokenService.HashRefreshToken(refreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null) return AuthError.InvalidRefreshToken;

        var now = time.GetUtcNow();
        var session = await db.Sessions.SingleAsync(s => s.Id == token.SessionId, ct);
        if (session.RevokedAt is not null || session.ExpiresAt <= now) return AuthError.InvalidRefreshToken;

        // A used token showing up again means someone copied it: end the whole session.
        if (token.UsedAt is not null) return await RevokeAsync(session.Id, RevokeReasons.RefreshReuse, now, ct);
        if (token.ExpiresAt <= now) return AuthError.InvalidRefreshToken;

        var user = await db.Users.SingleAsync(u => u.Id == session.UserId, ct);
        if (user.Status != UserStatus.Active) return await RevokeAsync(session.Id, RevokeReasons.UserInactive, now, ct);

        // Claiming the token is one conditional UPDATE: of two parallel refreshes only one gets a row.
        var claimed = await db.RefreshTokens.Where(t => t.Id == token.Id && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
        if (claimed == 0) return await RevokeAsync(session.Id, RevokeReasons.RefreshReuse, now, ct);

        var next = AddRefreshToken(session, now);
        session.LastSeenAt = now;
        session.ExpiresAt = now.AddDays(_o.RefreshTokenDays);
        await db.SaveChangesAsync(ct);
        return Respond(user, session.Id, next);
    }

    public async Task LogoutAsync(Guid sessionId, CancellationToken ct) =>
        await RevokeAsync(sessionId, RevokeReasons.Logout, time.GetUtcNow(), ct);

    public async Task<List<SessionSummary>> ListSessionsAsync(Guid userId, Guid currentSessionId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var sessions = await db.Sessions
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now)
            .OrderByDescending(s => s.LastSeenAt)
            .ToListAsync(ct);
        return sessions.Select(s => new SessionSummary(
            s.Id, s.DeviceName, s.UserAgent, s.IpAddress.ToString(), s.CreatedAt, s.LastSeenAt, s.Id == currentSessionId)).ToList();
    }

    /// <summary>Revokes one session and returns the refresh-token failure the caller usually wants to hand back.</summary>
    private async Task<AuthError> RevokeAsync(Guid sessionId, string reason, DateTimeOffset now, CancellationToken ct)
    {
        await db.Sessions.Where(s => s.Id == sessionId && s.RevokedAt == null).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.RevokedAt, now)
            .SetProperty(x => x.RevokeReason, reason), ct);
        return AuthError.InvalidRefreshToken;
    }

    // The increment is one UPDATE, so parallel guesses cannot slip past the counter.
    private async Task RegisterFailureAsync(User user, DateTimeOffset now, CancellationToken ct)
    {
        await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(
            s => s.SetProperty(u => u.FailedLoginCount, u => (short)(u.FailedLoginCount + 1)), ct);
        var count = await db.Users.Where(u => u.Id == user.Id).Select(u => u.FailedLoginCount).SingleAsync(ct);
        if (count < _o.MaxFailedLogins) return;

        var until = now.AddMinutes(_o.LockoutMinutes);
        await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.FailedLoginCount, (short)0)
            .SetProperty(u => u.LockedUntil, (DateTimeOffset?)until), ct);
    }

    private async Task<AuthResponse> StartSessionAsync(
        User user, string? deviceName, ClientInfo client, DateTimeOffset now, CancellationToken ct)
    {
        var session = new Session
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            DeviceName = string.IsNullOrWhiteSpace(deviceName) ? DefaultDeviceName : Truncate(deviceName.Trim(), 100),
            UserAgent = Truncate(client.UserAgent, 512),
            IpAddress = client.IpAddress,
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now.AddDays(_o.RefreshTokenDays),
        };
        db.Sessions.Add(session);
        var refresh = AddRefreshToken(session, now);
        await db.SaveChangesAsync(ct);
        return Respond(user, session.Id, refresh);
    }

    private string AddRefreshToken(Session session, DateTimeOffset now)
    {
        var (plain, hash) = TokenService.NewRefreshToken();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            TokenHash = hash,
            IssuedAt = now,
            ExpiresAt = now.AddDays(_o.RefreshTokenDays),
        });
        return plain;
    }

    private AuthResponse Respond(User user, Guid sessionId, string refreshToken) =>
        new(tokens.CreateAccessToken(user, sessionId), tokens.AccessTokenSeconds, refreshToken, Summary(user));

    public static UserSummary Summary(User u) =>
        new(u.Id, u.Username, u.Email, u.DisplayName, TokenService.RoleName(u.Role));

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

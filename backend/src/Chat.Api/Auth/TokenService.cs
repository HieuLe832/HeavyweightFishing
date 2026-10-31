using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Chat.Data;
using Chat.Data.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Chat.Api.Auth;

/// <summary>Access tokens (JWT) and refresh tokens (random, stored only as a SHA-256 hash).</summary>
public class TokenService(TimeProvider time, IOptions<AuthOptions> options)
{
    private readonly AuthOptions _o = options.Value;

    public int AccessTokenSeconds => _o.AccessTokenMinutes * 60;

    public string CreateAccessToken(User user, Guid sessionId)
    {
        var now = time.GetUtcNow();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _o.Issuer,
            Audience = _o.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.AddMinutes(_o.AccessTokenMinutes).UtcDateTime,
            Subject = new ClaimsIdentity([
                new Claim(AuthClaims.Subject, user.Id.ToString()),
                new Claim(AuthClaims.Role, RoleName(user.Role)),
                new Claim(AuthClaims.Session, sessionId.ToString()),
            ]),
            SigningCredentials = new SigningCredentials(SigningKey(_o), SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>A new refresh token: the plain value for the client and the hash for the database.</summary>
    public static (string Plain, byte[] Hash) NewRefreshToken()
    {
        var plain = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (plain, HashRefreshToken(plain));
    }

    public static byte[] HashRefreshToken(string plain) => SHA256.HashData(Encoding.UTF8.GetBytes(plain));

    public static string RoleName(UserRole role) => role.ToString().ToLowerInvariant();

    public static SymmetricSecurityKey SigningKey(AuthOptions o) => new(Encoding.UTF8.GetBytes(o.JwtKey));
}

public static class AuthClaims
{
    public const string Subject = "sub";
    public const string Role = "role";
    public const string Session = "sid";

    public static Guid UserId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(Subject)!);

    public static Guid SessionId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(Session)!);
}

public static class Policies
{
    public const string Admin = "Admin";
}

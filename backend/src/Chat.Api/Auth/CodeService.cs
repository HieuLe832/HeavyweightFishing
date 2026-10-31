using System.Security.Cryptography;
using System.Text;
using Chat.Data;
using Chat.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chat.Api.Auth;

/// <summary>Six-digit email codes (D3). Stored as a keyed HMAC, so a leaked table cannot be brute-forced offline.</summary>
public class CodeService(ChatDbContext db, TimeProvider time, IOptions<AuthOptions> options)
{
    private readonly AuthOptions _o = options.Value;

    /// <summary>Adds a new code to the context (the caller saves) and supersedes the older ones. Returns the plain code.</summary>
    public async Task<string> IssueAsync(Guid userId, VerificationPurpose purpose, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var older = await db.VerificationCodes
            .Where(c => c.UserId == userId && c.Purpose == purpose && c.ConsumedAt == null)
            .ToListAsync(ct);
        foreach (var c in older) c.ConsumedAt = now;

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        db.VerificationCodes.Add(new VerificationCode
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Purpose = purpose,
            CodeHash = Hash(userId, purpose, code),
            ExpiresAt = now.AddMinutes(_o.CodeMinutes),
            CreatedAt = now,
        });
        return code;
    }

    /// <summary>True if this user was issued a code of this kind within the resend cooldown.</summary>
    public Task<bool> WasIssuedRecentlyAsync(Guid userId, VerificationPurpose purpose, CancellationToken ct = default)
    {
        var since = time.GetUtcNow().AddSeconds(-_o.ResendCooldownSeconds);
        return db.VerificationCodes.AnyAsync(c => c.UserId == userId && c.Purpose == purpose && c.CreatedAt > since, ct);
    }

    /// <summary>
    /// Checks a guess against the newest live code and consumes it on a match. Both the attempt counter and the
    /// consumption are single conditional UPDATEs, so parallel guesses cannot exceed the attempt limit or use a code twice.
    /// </summary>
    public async Task<bool> CheckAsync(Guid userId, VerificationPurpose purpose, string code, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var max = (short)_o.CodeMaxAttempts;
        var candidate = await db.VerificationCodes
            .Where(c => c.UserId == userId && c.Purpose == purpose && c.ConsumedAt == null
                        && c.ExpiresAt > now && c.Attempts < max)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new { c.Id, c.CodeHash })
            .FirstOrDefaultAsync(ct);
        if (candidate is null) return false;

        var counted = await db.VerificationCodes
            .Where(c => c.Id == candidate.Id && c.ConsumedAt == null && c.Attempts < max)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Attempts, c => (short)(c.Attempts + 1)), ct);
        if (counted == 0) return false;

        var matches = CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(candidate.CodeHash), Encoding.ASCII.GetBytes(Hash(userId, purpose, code)));
        if (!matches) return false;

        var consumed = await db.VerificationCodes
            .Where(c => c.Id == candidate.Id && c.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), ct);
        return consumed == 1;
    }

    private string Hash(Guid userId, VerificationPurpose purpose, string code)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_o.CodeKey));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{userId}:{purpose}:{code}")));
    }
}

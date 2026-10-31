using Chat.Api.Email;
using Chat.Data;
using Chat.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Chat.Api.Auth;

/// <summary>Processes 1.1 Register, 1.2 Verify Email and 1.3 Reset Password.</summary>
public class AccountService(
    ChatDbContext db,
    CodeService codes,
    IPasswordHasher<User> hasher,
    IEmailSender email,
    TimeProvider time,
    ILogger<AccountService> log)
{
    public async Task<Result<Guid>> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var username = request.Username!.Trim();
        var address = request.Email!.Trim();

        if (await db.Users.AnyAsync(u => u.Email == address, ct)) return AuthError.EmailTaken;
        if (await db.Users.AnyAsync(u => u.Username == username, ct)) return AuthError.UsernameTaken;

        var now = time.GetUtcNow();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = address,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? username : request.DisplayName.Trim(),
            Role = UserRole.User,
            Status = UserStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };
        user.PasswordHash = hasher.HashPassword(user, request.Password!);
        db.Users.Add(user);
        var code = await codes.IssueAsync(user.Id, VerificationPurpose.VerifyEmail, ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            return pg.ConstraintName?.Contains("username") == true ? AuthError.UsernameTaken : AuthError.EmailTaken;
        }

        await SendAsync(address, EmailTemplates.VerifyEmail(code), ct);
        return user.Id;
    }

    public async Task<Result<bool>> VerifyEmailAsync(VerifyEmailRequest request, CancellationToken ct)
    {
        var address = request.Email!.Trim();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == address && u.Status == UserStatus.Pending, ct);
        if (user is null || !await codes.CheckAsync(user.Id, VerificationPurpose.VerifyEmail, request.Code!, ct))
            return AuthError.InvalidCode;

        var now = time.GetUtcNow();
        user.Status = UserStatus.Active;
        user.EmailVerifiedAt = now;
        user.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Sends a fresh code to a pending account. Silent for unknown, active or throttled requests.</summary>
    public async Task ResendVerificationAsync(string email, CancellationToken ct)
    {
        var address = email.Trim();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == address && u.Status == UserStatus.Pending, ct);
        if (user is null || await codes.WasIssuedRecentlyAsync(user.Id, VerificationPurpose.VerifyEmail, ct)) return;

        var code = await codes.IssueAsync(user.Id, VerificationPurpose.VerifyEmail, ct);
        await db.SaveChangesAsync(ct);
        await SendAsync(user.Email, EmailTemplates.VerifyEmail(code), ct);
    }

    // A failed send must not fail the request: the user can ask for a new code.
    private async Task SendAsync(string to, (string Subject, string Body) mail, CancellationToken ct)
    {
        try
        {
            await email.SendAsync(to, mail.Subject, mail.Body, ct);
        }
        catch (Exception e)
        {
            log.LogError(e, "Could not send email to {Address}", to);
        }
    }
}

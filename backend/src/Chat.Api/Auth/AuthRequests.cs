using System.Text.RegularExpressions;

namespace Chat.Api.Auth;

public record RegisterRequest(string? Username, string? Email, string? Password, string? DisplayName);

public record VerifyEmailRequest(string? Email, string? Code);

public record PasswordResetConfirmRequest(string? Email, string? Code, string? NewPassword);

public record EmailRequest(string? Email);

public record LoginRequest(string? Identifier, string? Password, string? DeviceName);

public record RefreshRequest(string? RefreshToken);

public record SessionSummary(
    Guid Id, string DeviceName, string UserAgent, string IpAddress,
    DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, bool Current);

public record UserSummary(Guid Id, string Username, string Email, string DisplayName, string Role);

public record AuthResponse(string AccessToken, int ExpiresIn, string RefreshToken, UserSummary User);

public static partial class AuthValidation
{
    public static Dictionary<string, string[]> Validate(RegisterRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        if (r.Username is null || !UsernamePattern().IsMatch(r.Username))
            errors["username"] = ["Use 3-32 letters, digits or underscores."];
        AddEmail(errors, r.Email);
        AddPassword(errors, "password", r.Password);
        if (r.DisplayName is { Length: > 64 }) errors["displayName"] = ["At most 64 characters."];
        return errors;
    }

    public static Dictionary<string, string[]> Validate(VerifyEmailRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        AddEmail(errors, r.Email);
        if (r.Code is null || !SixDigits().IsMatch(r.Code)) errors["code"] = ["Enter the 6-digit code."];
        return errors;
    }

    public static Dictionary<string, string[]> Validate(PasswordResetConfirmRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        AddEmail(errors, r.Email);
        if (r.Code is null || !SixDigits().IsMatch(r.Code)) errors["code"] = ["Enter the 6-digit code."];
        AddPassword(errors, "newPassword", r.NewPassword);
        return errors;
    }

    public static Dictionary<string, string[]> Validate(EmailRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        AddEmail(errors, r.Email);
        return errors;
    }

    public static Dictionary<string, string[]> Validate(LoginRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(r.Identifier) || r.Identifier.Length > 254)
            errors["identifier"] = ["Enter your username or email."];
        if (string.IsNullOrEmpty(r.Password) || r.Password.Length > 128)
            errors["password"] = ["Enter your password."];
        return errors;
    }

    public static Dictionary<string, string[]> Validate(RefreshRequest r) =>
        string.IsNullOrWhiteSpace(r.RefreshToken) || r.RefreshToken.Length > 256
            ? new() { ["refreshToken"] = ["Send the refresh token."] }
            : [];

    public static void AddEmail(Dictionary<string, string[]> errors, string? email)
    {
        if (email is null || email.Length > 254 || !EmailPattern().IsMatch(email))
            errors["email"] = ["Enter a valid email address."];
    }

    public static void AddPassword(Dictionary<string, string[]> errors, string field, string? password)
    {
        if (password is null || password.Length is < 8 or > 128)
            errors[field] = ["Use 8-128 characters."];
    }

    [GeneratedRegex(@"^\d{6}$")]
    private static partial Regex SixDigits();

    [GeneratedRegex(@"^[A-Za-z0-9_]{3,32}$")]
    private static partial Regex UsernamePattern();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();
}

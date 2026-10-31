namespace Chat.Api.Auth;

/// <summary>A failure a service reports to the endpoint, which turns it into a ProblemDetails response.</summary>
public record AuthError(int Status, string Code, string Title)
{
    public static readonly AuthError EmailTaken = new(409, "email_taken", "That email is already registered.");
    public static readonly AuthError UsernameTaken = new(409, "username_taken", "That username is taken.");
    public static readonly AuthError InvalidCode = new(400, "invalid_code", "That code is wrong or has expired.");
    public static readonly AuthError InvalidCredentials = new(401, "invalid_credentials", "Wrong username, email or password.");
    public static readonly AuthError EmailNotVerified = new(403, "email_not_verified", "Verify your email before signing in.");
    public static readonly AuthError AccountBanned = new(403, "account_banned", "This account has been banned.");
    public static readonly AuthError InvalidRefreshToken = new(401, "invalid_refresh_token", "Sign in again.");

    // Not auth failures, but the API shares this error shape (ProblemDetails with a `code`).
    public static readonly AuthError UserNotFound = new(404, "user_not_found", "No such user.");
    public static readonly AuthError Blocked = new(403, "blocked", "You blocked this user. Unblock them first.");
    public static readonly AuthError SelfTarget = new(400, "invalid_target", "You cannot do that to yourself.");
}

public readonly record struct Result<T>(T? Value, AuthError? Error)
{
    public static implicit operator Result<T>(T value) => new(value, null);
    public static implicit operator Result<T>(AuthError error) => new(default, error);
}

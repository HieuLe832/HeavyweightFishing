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
    public static readonly AuthError ConversationNotFound = new(404, "conversation_not_found", "No such conversation.");
    public static readonly AuthError MemberNotFound = new(404, "member_not_found", "That user is not in this group.");
    public static readonly AuthError NotAGroup = new(400, "not_a_group", "That only works in a group.");
    public static readonly AuthError InsufficientRole = new(403, "insufficient_role", "Your role in this group does not allow that.");
    public static readonly AuthError GroupFull = new(409, "group_full", "This group has reached its member limit.");
    public static readonly AuthError Conflict = new(409, "conflict", "Something changed at the same time. Try again.");
    public static readonly AuthError SessionEnded = new(401, "session_ended", "Your session has ended. Sign in again.");
    public static readonly AuthError MessageNotFound = new(404, "message_not_found", "No such message.");
    public static readonly AuthError MessageDeleted = new(409, "message_deleted", "That message was deleted.");
    public static readonly AuthError RateLimited = new(429, "rate_limited", "You are sending too fast. Slow down.");
    public static readonly AuthError UnsupportedType = new(400, "unsupported_type", "That kind of message is not supported yet.");
    public static readonly AuthError Invalid = new(400, "validation_failed", "That message is not valid.");
    public static readonly AuthError FileTooLarge = new(413, "file_too_large", "That file is too large.");
    public static readonly AuthError UnsupportedMediaType = new(415, "unsupported_media_type", "That kind of file is not allowed.");
    public static readonly AuthError MediaNotFound = new(404, "media_not_found", "No such file.");
    public static readonly AuthError AvatarProviderUnavailable = new(503, "avatar_provider_unavailable", "Character avatars are unavailable right now.");
    public static readonly AuthError AvatarNotFound = new(404, "avatar_not_found", "No such avatar.");
    public static readonly AuthError AvatarProviderInvalid = new(502, "avatar_provider_invalid", "The avatar provider sent something that is not an image.");
    public static readonly AuthError InvalidAttachment = new(400, "invalid_attachment", "One of the attachments cannot be used.");
    public static readonly AuthError ReportNotFound = new(404, "report_not_found", "No such report.");
    public static readonly AuthError ReportResolved = new(409, "report_resolved", "That report has already been decided.");
    public static readonly AuthError ForbiddenTarget = new(403, "forbidden_target", "You cannot do that to this account.");
    public static readonly AuthError AdminOnly = new(403, "admin_only", "Only admins can do that.");
    public static readonly AuthError SelfTarget = new(400, "invalid_target", "You cannot do that to yourself.");
}

public readonly record struct Result<T>(T? Value, AuthError? Error)
{
    public static implicit operator Result<T>(T value) => new(value, null);
    public static implicit operator Result<T>(AuthError error) => new(default, error);
    public static Result<T> FromError(AuthError error) => new(default, error);
}

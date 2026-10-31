namespace Chat.Api.Users;

public record UpdateProfileRequest(string? DisplayName);

public record ContactRequest(string? Nickname);

public record UserProfile(Guid Id, string Username, string DisplayName, bool IsContact, bool IsBlocked);

public record ContactSummary(Guid UserId, string Username, string DisplayName, string? Nickname, DateTimeOffset CreatedAt);

public record BlockSummary(Guid UserId, string Username, string DisplayName, DateTimeOffset CreatedAt);

public static class UserValidation
{
    public const int MinSearchLength = 2;
    public const int MaxSearchLength = 64;
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;

    public static Dictionary<string, string[]> Validate(UpdateProfileRequest r) =>
        string.IsNullOrWhiteSpace(r.DisplayName) || r.DisplayName.Trim().Length > 64
            ? new() { ["displayName"] = ["Use 1-64 characters."] }
            : [];

    public static Dictionary<string, string[]> Validate(ContactRequest? r) =>
        r?.Nickname is { } n && (n.Trim().Length is 0 or > 64)
            ? new() { ["nickname"] = ["Use 1-64 characters, or leave it out."] }
            : [];

    public static Dictionary<string, string[]> ValidateSearch(string? q, int? limit)
    {
        var errors = new Dictionary<string, string[]>();
        if (q is null || q.Trim().Length is < MinSearchLength or > MaxSearchLength)
            errors["q"] = [$"Use {MinSearchLength}-{MaxSearchLength} characters."];
        if (limit is < 1 or > MaxLimit) errors["limit"] = [$"Use 1-{MaxLimit}."];
        return errors;
    }
}

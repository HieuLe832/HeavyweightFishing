namespace Chat.Data.Entities;

// D1 Users

public class User
{
    public Guid Id { get; set; }
    public required string Username { get; set; }
    public required string Email { get; set; }
    public DateTimeOffset? EmailVerifiedAt { get; set; }
    public string? PasswordHash { get; set; }
    public required string DisplayName { get; set; }
    public Guid? AvatarMediaId { get; set; }
    /// <summary>A character from the Avatar Provider's pack. Never set together with <see cref="AvatarMediaId"/>.</summary>
    public string? AvatarPresetKey { get; set; }
    public UserRole Role { get; set; }
    public UserStatus Status { get; set; }
    public short FailedLoginCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class OAuthIdentity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string Provider { get; set; }
    public required string ProviderSubject { get; set; }
    public required string Email { get; set; }
    public DateTimeOffset LinkedAt { get; set; }
}

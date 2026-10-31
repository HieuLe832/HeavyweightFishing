using System.Net;

namespace Chat.Data.Entities;

// D2 Sessions & Devices, D3 Verification Codes

public class Session
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string DeviceName { get; set; }
    public required string UserAgent { get; set; }
    public required IPAddress IpAddress { get; set; }
    public PushPlatform? PushPlatform { get; set; }
    public string? PushToken { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }
}

public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public required byte[] TokenHash { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public class VerificationCode
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public VerificationPurpose Purpose { get; set; }
    public required string CodeHash { get; set; }
    public short Attempts { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

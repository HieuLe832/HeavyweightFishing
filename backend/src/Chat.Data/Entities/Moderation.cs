using System.Net;

namespace Chat.Data.Entities;

// D8 Reports & Audit Log

public class Report
{
    public Guid Id { get; set; }
    public Guid ReporterId { get; set; }
    public Guid TargetUserId { get; set; }
    public Guid? MessageId { get; set; }
    public ReportReason Reason { get; set; }
    public string? Details { get; set; }

    /// <summary>JSON snapshot of the reported message, kept even if the message is deleted.</summary>
    public required string Evidence { get; set; }

    public ReportStatus Status { get; set; }
    public Guid? ResolvedBy { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Append-only: the database rejects UPDATE and DELETE.</summary>
public class AuditLog
{
    public long Id { get; set; }
    public Guid? ActorId { get; set; }
    public required string Action { get; set; }
    public required string TargetType { get; set; }
    public required string TargetId { get; set; }
    public required string Metadata { get; set; }
    public required IPAddress IpAddress { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

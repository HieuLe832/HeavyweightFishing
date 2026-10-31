using System.Net;
using System.Text.Json;
using Chat.Data;
using Chat.Data.Entities;

namespace Chat.Api.Moderation;

public static class AuditActions
{
    public const string ViewEvidence = "report.view_evidence";
    public const string ReportAction = "report.action";
    public const string ReportDismiss = "report.dismiss";
    public const string Ban = "user.ban";
    public const string Unban = "user.unban";
}

/// <summary>
/// Process 6.3: the only code that adds to the audit log. The entry joins the caller's unit of work, so an action and its
/// audit entry are saved together or not at all. The database refuses to change or delete entries afterwards.
/// </summary>
public class AuditWriter(ChatDbContext db, IHttpContextAccessor http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Adds the entry without saving it.</summary>
    public void Add(Guid actorId, string action, string targetType, string targetId, object? metadata = null) =>
        db.AuditLogs.Add(new AuditLog
        {
            ActorId = actorId, Action = action, TargetType = targetType, TargetId = targetId,
            Metadata = JsonSerializer.Serialize(metadata ?? new { }, Json),
            IpAddress = http.HttpContext?.Connection.RemoteIpAddress ?? IPAddress.Loopback,
        });
}

using System.Text.Json;
using Chat.Api.Auth;
using Chat.Api.Conversations;
using Chat.Api.Messaging;
using Chat.Api.Users;
using Chat.Data;
using Chat.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chat.Api.Moderation;

public record SubmitReportRequest(Guid? MessageId, Guid? TargetUserId, string? Reason, string? Details);

public record ReportReceipt(Guid Id, string Status, DateTimeOffset CreatedAt);

/// <param name="Created">False when the caller already had an open report about the same thing.</param>
public record SubmitResult(ReportReceipt Report, bool Created);

public class ReportRateLimiter(IOptions<ModerationOptions> options, TimeProvider time)
    : SlidingWindowLimiter(() => (options.Value.ReportLimitCount, TimeSpan.FromSeconds(options.Value.ReportLimitWindowSeconds)), time);

/// <summary>Process 6.1: any user reports a message or a user. The report keeps a snapshot of the evidence.</summary>
public class ReportService(
    ChatDbContext db, ConversationReader conversations, UserAccess access, ReportRateLimiter limiter,
    IOptions<ModerationOptions> options, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public static Dictionary<string, string[]> Validate(SubmitReportRequest r, int maxDetails)
    {
        var errors = new Dictionary<string, string[]>();
        if (ParseReason(r.Reason) is null) errors["reason"] = ["Use spam, harassment or other."];
        if (r.Details is { } d && d.Length > maxDetails) errors["details"] = [$"At most {maxDetails} characters."];
        if (r.MessageId is null && r.TargetUserId is null) errors["messageId"] = ["Say which message or user you are reporting."];
        return errors;
    }

    public int MaxDetails => options.Value.MaxDetailsLength;

    public static ReportReason? ParseReason(string? reason) => reason?.ToLowerInvariant() switch
    {
        "spam" => ReportReason.Spam,
        "harassment" => ReportReason.Harassment,
        "other" => ReportReason.Other,
        _ => null,
    };

    public async Task<Result<SubmitResult>> SubmitAsync(Guid reporterId, SubmitReportRequest request, CancellationToken ct)
    {
        var reason = ParseReason(request.Reason)!.Value;
        Guid targetId;
        Guid? messageId = request.MessageId;
        object evidence;
        var now = time.GetUtcNow();

        if (messageId is { } id)
        {
            var message = await db.Messages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id && m.Type != MessageType.System, ct);
            if (message is null || await conversations.FindAsync(reporterId, message.ConversationId, ct) is not { } membership)
                return AuthError.MessageNotFound;
            if (message.SenderId is not { } sender || (request.TargetUserId is { } named && named != sender))
                return AuthError.Invalid;
            if (sender == reporterId) return AuthError.SelfTarget;
            if (message.DeletedAt is not null) return AuthError.MessageDeleted;

            targetId = sender;
            var attachments = (await MessageWriter.LoadAttachmentsAsync(db, [message.Id], ct)).GetValueOrDefault(message.Id) ?? [];
            evidence = new
            {
                kind = "message", capturedAt = now,
                conversation = new { id = membership.Conversation.Id, type = membership.Conversation.Type.ToString().ToLowerInvariant(), title = membership.Conversation.Title },
                message = MessageWriter.ToDto(message, null, attachments),
            };
        }
        else
        {
            var found = await access.FindVisibleAsync(reporterId, request.TargetUserId!.Value, ct);
            if (found.Error is { } error) return error;
            targetId = found.Value!.Id;
            evidence = new { kind = "user", capturedAt = now, user = new { id = found.Value.Id, username = found.Value.Username, displayName = found.Value.DisplayName } };
        }

        var open = await db.Reports.AsNoTracking().FirstOrDefaultAsync(r =>
            r.ReporterId == reporterId && r.Status == ReportStatus.Open && r.MessageId == messageId && (messageId != null || r.TargetUserId == targetId), ct);
        if (open is not null) return new SubmitResult(Receipt(open), false);

        if (!limiter.TryAcquire(reporterId)) return AuthError.RateLimited;

        var report = new Report
        {
            Id = Guid.NewGuid(), ReporterId = reporterId, TargetUserId = targetId, MessageId = messageId, Reason = reason,
            Details = string.IsNullOrWhiteSpace(request.Details) ? null : request.Details.Trim(),
            Evidence = JsonSerializer.Serialize(evidence, Json), Status = ReportStatus.Open, CreatedAt = now,
        };
        db.Reports.Add(report);
        await db.SaveChangesAsync(ct);
        return new SubmitResult(Receipt(report), true);
    }

    private static ReportReceipt Receipt(Report r) => new(r.Id, r.Status.ToString().ToLowerInvariant(), r.CreatedAt);
}

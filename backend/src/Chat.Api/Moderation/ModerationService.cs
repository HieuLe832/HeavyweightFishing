using System.Text.Json;
using Chat.Api.Auth;
using Chat.Data;
using Chat.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Moderation;

public record AccountBrief(Guid Id, string Username, string DisplayName, string Status);

public record ReportRow(
    Guid Id, string Reason, string Status, string? Details, Guid? MessageId, DateTimeOffset CreatedAt,
    AccountBrief Reporter, AccountBrief Target, DateTimeOffset? ResolvedAt, Guid? ResolvedBy);

public record ReportDetail(
    Guid Id, string Reason, string Status, string? Details, Guid? MessageId, DateTimeOffset CreatedAt,
    AccountBrief Reporter, AccountBrief Target, DateTimeOffset? ResolvedAt, Guid? ResolvedBy, JsonElement Evidence);

public record ResolveRequest(string? Decision, bool? Ban);

public record BanRequest(string? Reason);

public record AuditRow(
    long Id, Guid? ActorId, string Action, string TargetType, string TargetId, JsonElement Metadata, string IpAddress, DateTimeOffset CreatedAt);

/// <summary>Processes 6.2 Review Reports, 6.4 Apply Moderation Decision and 6.5 Query Audit Log. Callers have already passed the admin check.</summary>
public class ModerationService(ChatDbContext db, AuditWriter audit, TimeProvider time)
{
    public const int DefaultReportLimit = 20, MaxReportLimit = 50, DefaultAuditLimit = 50, MaxAuditLimit = 100;

    // --- 6.2 ---

    /// <summary>Reports oldest first, a queue. The evidence is left out: <see cref="GetAsync"/> is the audited way to read it.</summary>
    public async Task<List<ReportRow>> ListAsync(ReportStatus? status, int limit, Guid? afterId, CancellationToken ct)
    {
        var query = db.Reports.AsNoTracking().Where(r => status == null || r.Status == status);
        if (afterId is { } after)
        {
            var cursor = await db.Reports.AsNoTracking().Where(r => r.Id == after).Select(r => new { r.CreatedAt, r.Id }).SingleOrDefaultAsync(ct);
            if (cursor is not null) query = query.Where(r => r.CreatedAt > cursor.CreatedAt || (r.CreatedAt == cursor.CreatedAt && r.Id.CompareTo(cursor.Id) > 0));
        }

        var rows = await query.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).Take(limit).ToListAsync(ct);
        var people = await PeopleAsync(rows, ct);
        return rows.Select(r => new ReportRow(
            r.Id, Name(r.Reason), Name(r.Status), r.Details, r.MessageId, r.CreatedAt,
            people[r.ReporterId], people[r.TargetUserId], r.ResolvedAt, r.ResolvedBy)).ToList();
    }

    /// <summary>One report with its evidence. Admins are reading private messages, so each read is written to the audit log.</summary>
    public async Task<Result<ReportDetail>> GetAsync(Guid adminId, Guid reportId, CancellationToken ct)
    {
        var report = await db.Reports.AsNoTracking().SingleOrDefaultAsync(r => r.Id == reportId, ct);
        if (report is null) return AuthError.ReportNotFound;

        audit.Add(adminId, AuditActions.ViewEvidence, "report", reportId.ToString(), new { reason = Name(report.Reason), report.TargetUserId });
        await db.SaveChangesAsync(ct);

        var people = await PeopleAsync([report], ct);
        return new ReportDetail(
            report.Id, Name(report.Reason), Name(report.Status), report.Details, report.MessageId, report.CreatedAt,
            people[report.ReporterId], people[report.TargetUserId], report.ResolvedAt, report.ResolvedBy,
            JsonSerializer.Deserialize<JsonElement>(report.Evidence));
    }

    // --- 6.4 ---

    /// <summary>Closes an open report. <paramref name="ban"/> also bans its target, in the same transaction.</summary>
    public async Task<AuthError?> ResolveAsync(Guid adminId, Guid reportId, bool action, bool ban, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var report = await db.Reports.AsNoTracking().SingleOrDefaultAsync(r => r.Id == reportId, ct);
        if (report is null) return AuthError.ReportNotFound;

        var now = time.GetUtcNow();
        var status = action ? ReportStatus.Actioned : ReportStatus.Dismissed;
        // The condition is in the UPDATE, so two admins deciding at once cannot both win.
        var closed = await db.Reports.Where(r => r.Id == reportId && r.Status == ReportStatus.Open).ExecuteUpdateAsync(u => u
            .SetProperty(r => r.Status, status).SetProperty(r => r.ResolvedBy, adminId).SetProperty(r => r.ResolvedAt, now), ct);
        if (closed == 0) return AuthError.ReportResolved;

        audit.Add(adminId, action ? AuditActions.ReportAction : AuditActions.ReportDismiss, "report", reportId.ToString(),
            new { targetUserId = report.TargetUserId, banned = action && ban });
        if (action && ban && await BanCoreAsync(adminId, report.TargetUserId, null, reportId, ct) is { } error) return error;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return null;
    }

    public async Task<AuthError?> BanAsync(Guid adminId, Guid userId, string? reason, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await BanCoreAsync(adminId, userId, reason, null, ct) is { } error) return error;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return null;
    }

    public async Task<AuthError?> UnbanAsync(Guid adminId, Guid userId, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return AuthError.UserNotFound;
        if (user.Status != UserStatus.Banned) return null;

        // Someone banned before verifying their email goes back to pending, not straight to active.
        user.Status = user.EmailVerifiedAt is null ? UserStatus.Pending : UserStatus.Active;
        user.UpdatedAt = time.GetUtcNow();
        audit.Add(adminId, AuditActions.Unban, "user", userId.ToString(), new { restoredTo = user.Status.ToString().ToLowerInvariant() });
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// Sets the account to banned and revokes every session, so the user is signed out everywhere at once. Saves, and leaves
    /// the commit to the caller's transaction. Already banned is a no-op with no second audit entry.
    /// </summary>
    private async Task<AuthError?> BanCoreAsync(Guid adminId, Guid userId, string? reason, Guid? reportId, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return AuthError.UserNotFound;
        if (user.Id == adminId || user.Role == UserRole.Admin) return AuthError.ForbiddenTarget;
        if (user.Status == UserStatus.Banned) return null;

        var now = time.GetUtcNow();
        user.Status = UserStatus.Banned;
        user.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await db.Sessions.Where(s => s.UserId == userId && s.RevokedAt == null).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.RevokedAt, now).SetProperty(x => x.RevokeReason, RevokeReasons.Banned), ct);
        audit.Add(adminId, AuditActions.Ban, "user", userId.ToString(), new { reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), reportId });
        return null;
    }

    // --- 6.5 ---

    /// <summary>Newest first. <paramref name="before"/> is the id of the last entry seen.</summary>
    public async Task<List<AuditRow>> QueryAuditAsync(
        Guid? actorId, string? action, DateTimeOffset? from, DateTimeOffset? to, int limit, long? before, CancellationToken ct)
    {
        var rows = await db.AuditLogs.AsNoTracking()
            .Where(a => (actorId == null || a.ActorId == actorId) && (action == null || a.Action == action)
                && (from == null || a.CreatedAt >= from) && (to == null || a.CreatedAt < to) && (before == null || a.Id < before))
            .OrderByDescending(a => a.Id).Take(limit).ToListAsync(ct);
        return rows.Select(a => new AuditRow(
            a.Id, a.ActorId, a.Action, a.TargetType, a.TargetId, JsonSerializer.Deserialize<JsonElement>(a.Metadata),
            a.IpAddress.ToString(), a.CreatedAt)).ToList();
    }

    private async Task<Dictionary<Guid, AccountBrief>> PeopleAsync(IReadOnlyCollection<Report> reports, CancellationToken ct)
    {
        var ids = reports.SelectMany(r => new[] { r.ReporterId, r.TargetUserId }).Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Username, u.DisplayName, u.Status }).ToDictionaryAsync(
                u => u.Id, u => new AccountBrief(u.Id, u.Username, u.DisplayName, Name(u.Status)), ct);
    }

    private static string Name<T>(T value) where T : Enum => value.ToString().ToLowerInvariant();
}

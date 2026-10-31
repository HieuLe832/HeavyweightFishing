using System.Net;
using System.Text.Json;
using Chat.Data;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using static Chat.Api.Tests.HubTestHelpers;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class AdminModerationTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    /// <summary>A reporter, an abuser who sent one message, and an open report about it.</summary>
    private async Task<(Person Reporter, Person Abuser, Guid ReportId, Guid MessageId)> OpenReportAsync(string reason = "spam")
    {
        var reporter = await api.CreatePersonAsync();
        var abuser = await api.CreatePersonAsync();
        var chat = await api.OpenDirectAsync(reporter, abuser);
        await using var abuserHub = await api.ConnectAsync(abuser);
        var sent = await SendMessageTests.SendAsync(abuserHub, chat, "bad words " + Guid.NewGuid().ToString("N")[..6]);
        var messageId = sent.GetProperty("id").GetGuid();
        return (reporter, abuser, await api.ReportedMessageAsync(reporter, messageId, reason), messageId);
    }

    private Task<HttpResponseMessage> ResolveAsync(Person admin, Guid reportId, object body) =>
        api.Client.PostAsync(admin, $"/admin/reports/{reportId}/resolve", body);

    private Task<HttpResponseMessage> BanAsync(Person admin, Guid userId, object? body = null) =>
        api.Client.PostAsync(admin, $"/admin/users/{userId}/ban", body ?? new { });

    // --- the gate ---

    [Fact]
    public async Task Admin_routes_need_an_admin()
    {
        var admin = await api.CreateAdminAsync();
        var user = await api.CreatePersonAsync();
        var (_, _, reportId, _) = await OpenReportAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.NewClient().GetAsync("/admin/reports")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client.GetAsync(user, "/admin/reports")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client.GetAsync(user, $"/admin/reports/{reportId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client.GetAsync(user, "/admin/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await BanAsync(user, admin.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Client.GetAsync(admin, "/admin/reports")).StatusCode);
    }

    [Fact]
    public async Task A_demoted_admin_is_refused_even_with_an_unexpired_token()
    {
        var admin = await api.CreateAdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await api.Client.GetAsync(admin, "/admin/reports")).StatusCode);

        await api.WithDbAsync(db => db.Users.Where(u => u.Id == admin.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, UserRole.User)));

        await (await api.Client.GetAsync(admin, "/admin/reports")).AssertErrorAsync(HttpStatusCode.Forbidden, "admin_only");
    }

    // --- review ---

    [Fact]
    public async Task The_queue_lists_open_reports_oldest_first_without_evidence_and_pages()
    {
        var admin = await api.CreateAdminAsync();
        await api.WithDbAsync(db => db.Reports.Where(r => r.Status == ReportStatus.Open).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReportStatus.Dismissed)));
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add((await OpenReportAsync()).ReportId);
            api.Time.Advance(TimeSpan.FromSeconds(1));
        }

        var first = await (await api.Client.GetAsync(admin, "/admin/reports?limit=2")).JsonAsync();
        var second = await (await api.Client.GetAsync(admin, $"/admin/reports?limit=2&after={ids[1]}")).JsonAsync();

        Assert.Equal(ids.Take(2), first.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));
        Assert.Equal(ids.Skip(2), second.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));
        var row = first[0];
        Assert.False(row.TryGetProperty("evidence", out _));
        Assert.Equal("open", row.GetProperty("status").GetString());
        Assert.Equal("spam", row.GetProperty("reason").GetString());
        Assert.Equal("active", row.GetProperty("target").GetProperty("status").GetString());
        Assert.NotEqual(row.GetProperty("reporter").GetProperty("id").GetGuid(), row.GetProperty("target").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task The_status_filter_and_limits_are_validated()
    {
        var admin = await api.CreateAdminAsync();
        var (_, _, done, _) = await OpenReportAsync();
        (await ResolveAsync(admin, done, new { decision = "dismiss" })).EnsureSuccessStatusCode();

        var dismissed = await (await api.Client.GetAsync(admin, "/admin/reports?status=dismissed&limit=50")).JsonAsync();
        var all = await (await api.Client.GetAsync(admin, "/admin/reports?status=all&limit=50")).JsonAsync();
        var open = await (await api.Client.GetAsync(admin, "/admin/reports?limit=50")).JsonAsync();

        Assert.Contains(dismissed.EnumerateArray(), r => r.GetProperty("id").GetGuid() == done);
        Assert.Contains(all.EnumerateArray(), r => r.GetProperty("id").GetGuid() == done);
        Assert.DoesNotContain(open.EnumerateArray(), r => r.GetProperty("id").GetGuid() == done);
        await (await api.Client.GetAsync(admin, "/admin/reports?status=weird")).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
        await (await api.Client.GetAsync(admin, "/admin/reports?limit=0")).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
        await (await api.Client.GetAsync(admin, "/admin/reports?limit=51")).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Reading_the_evidence_is_audited_every_time()
    {
        var admin = await api.CreateAdminAsync();
        var (_, abuser, reportId, messageId) = await OpenReportAsync("harassment");

        var detail = await (await api.Client.GetAsync(admin, $"/admin/reports/{reportId}")).JsonAsync();
        await api.Client.GetAsync(admin, $"/admin/reports/{reportId}");

        Assert.StartsWith("bad words", detail.GetProperty("evidence").GetProperty("message").GetProperty("body").GetString());
        Assert.Equal(messageId, detail.GetProperty("evidence").GetProperty("message").GetProperty("id").GetGuid());
        Assert.Equal(abuser.Id, detail.GetProperty("target").GetProperty("id").GetGuid());
        var entries = await api.AuditEntriesAsync("report.view_evidence", reportId.ToString());
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.Equal(admin.Id, e.ActorId);
            Assert.Equal("report", e.TargetType);
            Assert.Equal("harassment", JsonDocument.Parse(e.Metadata).RootElement.GetProperty("reason").GetString());
            Assert.NotNull(e.IpAddress);
        });
        await (await api.Client.GetAsync(admin, $"/admin/reports/{Guid.NewGuid()}")).AssertErrorAsync(HttpStatusCode.NotFound, "report_not_found");
    }

    // --- decide ---

    [Fact]
    public async Task A_report_is_actioned_or_dismissed_once()
    {
        var admin = await api.CreateAdminAsync();
        var (_, abuser, actioned, _) = await OpenReportAsync();
        var (_, _, dismissed, _) = await OpenReportAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await ResolveAsync(admin, actioned, new { decision = "action" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ResolveAsync(admin, dismissed, new { decision = "Dismiss" })).StatusCode);

        var report = await api.WithDbAsync(db => db.Reports.AsNoTracking().SingleAsync(r => r.Id == actioned));
        Assert.Equal(ReportStatus.Actioned, report.Status);
        Assert.Equal(admin.Id, report.ResolvedBy);
        Assert.Equal(api.Time.GetUtcNow(), report.ResolvedAt);
        Assert.Equal(ReportStatus.Dismissed, (await api.WithDbAsync(db => db.Reports.AsNoTracking().SingleAsync(r => r.Id == dismissed))).Status);
        await (await ResolveAsync(admin, actioned, new { decision = "dismiss" })).AssertErrorAsync(HttpStatusCode.Conflict, "report_resolved");
        Assert.Single(await api.AuditEntriesAsync("report.action", actioned.ToString()));
        Assert.Single(await api.AuditEntriesAsync("report.dismiss", dismissed.ToString()));
        Assert.Equal(UserStatus.Active, (await api.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == abuser.Id))).Status); // no ban asked for
    }

    [Fact]
    public async Task Bad_decisions_are_validation_failures_and_an_unknown_report_is_404()
    {
        var admin = await api.CreateAdminAsync();
        var (_, _, reportId, _) = await OpenReportAsync();

        await (await ResolveAsync(admin, reportId, new { decision = "ignore" })).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
        await (await ResolveAsync(admin, reportId, new { })).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
        await (await ResolveAsync(admin, reportId, new { decision = "dismiss", ban = true })).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
        await (await ResolveAsync(admin, Guid.NewGuid(), new { decision = "action" })).AssertErrorAsync(HttpStatusCode.NotFound, "report_not_found");
        Assert.Equal(ReportStatus.Open, (await api.WithDbAsync(db => db.Reports.AsNoTracking().SingleAsync(r => r.Id == reportId))).Status);
    }

    [Fact]
    public async Task Actioning_with_ban_bans_the_target_in_one_step_and_audits_both()
    {
        var admin = await api.CreateAdminAsync();
        var (_, abuser, reportId, _) = await OpenReportAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await ResolveAsync(admin, reportId, new { decision = "action", ban = true })).StatusCode);

        Assert.Equal(UserStatus.Banned, (await api.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == abuser.Id))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client.GetAsync(abuser, "/auth/me")).StatusCode);
        var ban = Assert.Single(await api.AuditEntriesAsync("user.ban", abuser.Id.ToString()));
        Assert.Equal(reportId, JsonDocument.Parse(ban.Metadata).RootElement.GetProperty("reportId").GetGuid());
        Assert.Single(await api.AuditEntriesAsync("report.action", reportId.ToString()));
    }

    [Fact]
    public async Task A_ban_that_is_refused_leaves_the_report_open()
    {
        var admin = await api.CreateAdminAsync();
        var otherAdmin = await api.CreateAdminAsync();
        var reporter = await api.CreatePersonAsync();
        var report = (await (await api.ReportAsync(reporter, new { targetUserId = otherAdmin.Id, reason = "other" })).JsonAsync()).GetProperty("id").GetGuid();

        await (await ResolveAsync(admin, report, new { decision = "action", ban = true })).AssertErrorAsync(HttpStatusCode.Forbidden, "forbidden_target");

        Assert.Equal(ReportStatus.Open, (await api.WithDbAsync(db => db.Reports.AsNoTracking().SingleAsync(r => r.Id == report))).Status);
        Assert.Empty(await api.AuditEntriesAsync("report.action", report.ToString()));
    }

    // --- ban and unban ---

    [Fact]
    public async Task A_ban_signs_the_user_out_everywhere_and_blocks_sign_in()
    {
        var admin = await api.CreateAdminAsync();
        var user = await api.CreatePersonAsync();
        var phone = await api.Client.LoginAsync(user.Username);
        var phoneBody = await phone.JsonAsync();
        var phoneToken = phoneBody.GetProperty("accessToken").GetString()!;
        var refresh = phoneBody.GetProperty("refreshToken").GetString()!;

        Assert.Equal(HttpStatusCode.NoContent, (await BanAsync(admin, user.Id, new { reason = "  spamming  " })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client.GetAsync(user, "/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client.GetAuthedAsync("/auth/me", phoneToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client.RefreshAsync(refresh)).StatusCode);
        await (await api.Client.LoginAsync(user.Username)).AssertErrorAsync(HttpStatusCode.Forbidden, "account_banned");
        var sessions = await api.WithDbAsync(db => db.Sessions.AsNoTracking().Where(s => s.UserId == user.Id).ToListAsync());
        Assert.All(sessions, s => { Assert.NotNull(s.RevokedAt); Assert.Equal("banned", s.RevokeReason); });
        var entry = Assert.Single(await api.AuditEntriesAsync("user.ban", user.Id.ToString()));
        Assert.Equal(admin.Id, entry.ActorId);
        Assert.Equal("spamming", JsonDocument.Parse(entry.Metadata).RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Banning_twice_does_nothing_the_second_time_and_admins_cannot_be_banned()
    {
        var admin = await api.CreateAdminAsync();
        var otherAdmin = await api.CreateAdminAsync();
        var user = await api.CreatePersonAsync();

        (await BanAsync(admin, user.Id)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await BanAsync(admin, user.Id)).StatusCode);

        Assert.Single(await api.AuditEntriesAsync("user.ban", user.Id.ToString()));
        await (await BanAsync(admin, admin.Id)).AssertErrorAsync(HttpStatusCode.Forbidden, "forbidden_target");
        await (await BanAsync(admin, otherAdmin.Id)).AssertErrorAsync(HttpStatusCode.Forbidden, "forbidden_target");
        await (await BanAsync(admin, Guid.NewGuid())).AssertErrorAsync(HttpStatusCode.NotFound, "user_not_found");
        await (await BanAsync(admin, user.Id, new { reason = new string('r', 501) })).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Unbanning_restores_the_account_and_a_pending_one_goes_back_to_pending()
    {
        var admin = await api.CreateAdminAsync();
        var verified = await api.CreatePersonAsync();
        var unverified = await api.CreatePersonAsync();
        await api.WithDbAsync(db => db.Users.Where(u => u.Id == unverified.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailVerifiedAt, (DateTimeOffset?)null)));
        (await BanAsync(admin, verified.Id)).EnsureSuccessStatusCode();
        (await BanAsync(admin, unverified.Id)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await api.Client.PostAsync(admin, $"/admin/users/{verified.Id}/unban")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.Client.PostAsync(admin, $"/admin/users/{unverified.Id}/unban")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.Client.PostAsync(admin, $"/admin/users/{verified.Id}/unban")).StatusCode); // already fine

        Assert.Equal(UserStatus.Active, (await api.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == verified.Id))).Status);
        Assert.Equal(UserStatus.Pending, (await api.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == unverified.Id))).Status);
        Assert.Equal(HttpStatusCode.OK, (await api.Client.LoginAsync(verified.Username)).StatusCode);
        Assert.Single(await api.AuditEntriesAsync("user.unban", verified.Id.ToString()));
        await (await api.Client.PostAsync(admin, $"/admin/users/{Guid.NewGuid()}/unban")).AssertErrorAsync(HttpStatusCode.NotFound, "user_not_found");
    }

    [Fact]
    public async Task A_banned_users_open_socket_stops_receiving_and_cannot_send()
    {
        var admin = await api.CreateAdminAsync();
        var banned = await api.CreatePersonAsync();
        var friend = await api.CreatePersonAsync();
        var chat = await api.OpenDirectAsync(banned, friend);
        await using var bannedHub = await api.ConnectAsync(banned);
        await using var friendHub = await api.ConnectAsync(friend);
        var bannedInbox = Inbox.Listen(bannedHub);
        var friendInbox = Inbox.Listen(friendHub);
        await SendMessageTests.SendAsync(friendHub, chat, "before");
        await bannedInbox.Next("message.created");

        (await BanAsync(admin, banned.Id)).EnsureSuccessStatusCode();
        await SendMessageTests.SendAsync(friendHub, chat, "after");

        await friendInbox.Next("message.created");
        await bannedInbox.ExpectNone("message.created");
        await AssertHubErrorAsync(() => SendMessageTests.SendAsync(bannedHub, chat, "still here?"), "session_ended");
    }

    // --- audit log ---

    [Fact]
    public async Task The_audit_log_is_searchable_by_actor_action_and_time_and_pages_newest_first()
    {
        var admin = await api.CreateAdminAsync();
        var other = await api.CreateAdminAsync();
        var first = await api.CreatePersonAsync();
        var second = await api.CreatePersonAsync();
        (await BanAsync(admin, first.Id)).EnsureSuccessStatusCode();
        // The log is stamped by the database clock (not the fake one), so time filters are tested against the real clock.
        await Task.Delay(100);
        var midpoint = DateTimeOffset.UtcNow;
        await Task.Delay(100);
        (await BanAsync(other, second.Id)).EnsureSuccessStatusCode();
        (await api.Client.PostAsync(admin, $"/admin/users/{first.Id}/unban")).EnsureSuccessStatusCode();

        var byAdmin = await AuditAsync(admin, $"actorId={admin.Id}&limit=100");
        var bans = await AuditAsync(admin, $"action=user.ban&actorId={other.Id}");
        var late = await AuditAsync(admin, $"from={Uri.EscapeDataString(midpoint.ToString("O"))}&actorId={admin.Id}");
        var early = await AuditAsync(admin, $"to={Uri.EscapeDataString(midpoint.ToString("O"))}&actorId={admin.Id}");

        Assert.Equal(["user.unban", "user.ban"], byAdmin.Select(e => e.GetProperty("action").GetString()).Take(2));
        Assert.Equal(second.Id.ToString(), Assert.Single(bans).GetProperty("targetId").GetString());
        Assert.Equal(["user.unban"], late.Select(e => e.GetProperty("action").GetString()));
        Assert.DoesNotContain(early, e => e.GetProperty("action").GetString() == "user.unban");
        var newest = byAdmin[0];
        Assert.Equal(admin.Id, newest.GetProperty("actorId").GetGuid());
        Assert.Equal("user", newest.GetProperty("targetType").GetString());
        Assert.False(string.IsNullOrEmpty(newest.GetProperty("ipAddress").GetString()));

        var page = await AuditAsync(admin, $"actorId={admin.Id}&limit=1");
        var next = await AuditAsync(admin, $"actorId={admin.Id}&limit=1&before={page[0].GetProperty("id").GetInt64()}");
        Assert.True(next[0].GetProperty("id").GetInt64() < page[0].GetProperty("id").GetInt64());
        await (await api.Client.GetAsync(admin, "/admin/audit?limit=0")).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
        await (await api.Client.GetAsync(admin, "/admin/audit?limit=101")).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    private async Task<List<JsonElement>> AuditAsync(Person admin, string query)
    {
        var response = await api.Client.GetAsync(admin, "/admin/audit?" + query);
        response.EnsureSuccessStatusCode();
        return (await response.JsonAsync()).EnumerateArray().ToList();
    }
}

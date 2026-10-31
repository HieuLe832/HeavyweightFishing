using System.Net;
using System.Text.Json;
using Chat.Data;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using static Chat.Api.Tests.HubTestHelpers;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class ReportTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private async Task<(Person Reporter, Person Abuser, Guid Chat, HubConnection AbuserHub)> DirectChatAsync()
    {
        var reporter = await api.CreatePersonAsync();
        var abuser = await api.CreatePersonAsync();
        var chat = await api.OpenDirectAsync(reporter, abuser);
        return (reporter, abuser, chat, await api.ConnectAsync(abuser));
    }

    private static Guid IdOf(JsonElement message) => message.GetProperty("id").GetGuid();

    // --- messages ---

    [Fact]
    public async Task A_reported_message_is_stored_with_a_snapshot_as_evidence()
    {
        var (reporter, abuser, chat, abuserHub) = await DirectChatAsync();
        var photo = await api.UploadedAsync(abuser);
        var sent = await abuserHub.SendMediaAsync(chat, [photo], "buy my stuff");

        var response = await api.ReportAsync(reporter, new { messageId = IdOf(sent), reason = "Spam", details = "  every day  " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal("open", body.GetProperty("status").GetString());
        var report = await api.WithDbAsync(db => db.Reports.AsNoTracking().SingleAsync(r => r.Id == body.GetProperty("id").GetGuid()));
        Assert.Equal(reporter.Id, report.ReporterId);
        Assert.Equal(abuser.Id, report.TargetUserId);
        Assert.Equal(IdOf(sent), report.MessageId);
        Assert.Equal(ReportReason.Spam, report.Reason);
        Assert.Equal("every day", report.Details);
        var evidence = JsonDocument.Parse(report.Evidence).RootElement;
        Assert.Equal("message", evidence.GetProperty("kind").GetString());
        Assert.Equal("buy my stuff", evidence.GetProperty("message").GetProperty("body").GetString());
        Assert.Equal(abuser.Id, evidence.GetProperty("message").GetProperty("senderId").GetGuid());
        Assert.Equal(1, evidence.GetProperty("message").GetProperty("seq").GetInt64());
        Assert.Equal(photo, evidence.GetProperty("message").GetProperty("attachments")[0].GetProperty("mediaId").GetGuid());
        Assert.Equal("direct", evidence.GetProperty("conversation").GetProperty("type").GetString());
    }

    [Fact]
    public async Task The_evidence_survives_the_message_being_deleted()
    {
        var (reporter, _, chat, abuserHub) = await DirectChatAsync();
        var sent = await SendMessageTests.SendAsync(abuserHub, chat, "something awful");
        var reportId = await api.ReportedMessageAsync(reporter, IdOf(sent), "harassment");

        await abuserHub.InvokeAsync("DeleteMessage", IdOf(sent));

        var report = await api.WithDbAsync(db => db.Reports.AsNoTracking().SingleAsync(r => r.Id == reportId));
        Assert.Equal("something awful", JsonDocument.Parse(report.Evidence).RootElement.GetProperty("message").GetProperty("body").GetString());
        var message = await api.WithDbAsync(db => db.Messages.AsNoTracking().SingleAsync(m => m.Id == IdOf(sent)));
        Assert.Null(message.Body); // the message itself is gone
    }

    [Fact]
    public async Task Someone_who_blocked_the_sender_can_still_report_them()
    {
        var (reporter, abuser, chat, abuserHub) = await DirectChatAsync();
        var sent = await SendMessageTests.SendAsync(abuserHub, chat, "you again");
        (await api.Client.PutAsync(reporter, $"/blocks/{abuser.Id}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Created, (await api.ReportAsync(reporter, new { messageId = IdOf(sent), reason = "harassment" })).StatusCode);
    }

    [Fact]
    public async Task Messages_a_member_may_not_report_are_refused()
    {
        var (reporter, abuser, chat, abuserHub) = await DirectChatAsync();
        var stranger = await api.CreatePersonAsync();
        var sent = await SendMessageTests.SendAsync(abuserHub, chat, "hi");
        var group = await api.CreateGroupAsync(reporter, abuser);
        (await api.Client.PostAsync(reporter, $"/conversations/{group}/members", new { userIds = new[] { stranger.Id } })).EnsureSuccessStatusCode();
        var system = await api.WithDbAsync(db => db.Messages.Where(m => m.ConversationId == group && m.Type == MessageType.System).Select(m => m.Id).FirstAsync());
        var own = await SendMessageTests.SendAsync(await api.ConnectAsync(reporter), chat, "mine");
        var deleted = await SendMessageTests.SendAsync(abuserHub, chat, "gone");
        await abuserHub.InvokeAsync("DeleteMessage", IdOf(deleted));

        await (await api.ReportAsync(stranger, new { messageId = IdOf(sent), reason = "spam" })).AssertErrorAsync(HttpStatusCode.NotFound, "message_not_found");
        await (await api.ReportAsync(reporter, new { messageId = Guid.NewGuid(), reason = "spam" })).AssertErrorAsync(HttpStatusCode.NotFound, "message_not_found");
        await (await api.ReportAsync(reporter, new { messageId = system, reason = "spam" })).AssertErrorAsync(HttpStatusCode.NotFound, "message_not_found");
        await (await api.ReportAsync(reporter, new { messageId = IdOf(own), reason = "spam" })).AssertErrorAsync(HttpStatusCode.BadRequest, "invalid_target");
        await (await api.ReportAsync(reporter, new { messageId = IdOf(deleted), reason = "spam" })).AssertErrorAsync(HttpStatusCode.Conflict, "message_deleted");
        await (await api.ReportAsync(reporter, new { messageId = IdOf(sent), targetUserId = reporter.Id, reason = "spam" })).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(HttpStatusCode.Created, (await api.ReportAsync(reporter, new { messageId = IdOf(sent), targetUserId = abuser.Id, reason = "spam" })).StatusCode);
    }

    [Fact]
    public async Task Reporting_the_same_thing_twice_returns_the_open_report()
    {
        var (reporter, _, chat, abuserHub) = await DirectChatAsync();
        var first = await SendMessageTests.SendAsync(abuserHub, chat, "one");
        var second = await SendMessageTests.SendAsync(abuserHub, chat, "two");
        var id = await api.ReportedMessageAsync(reporter, IdOf(first));

        var again = await api.ReportAsync(reporter, new { messageId = IdOf(first), reason = "harassment" });
        var other = await api.ReportAsync(reporter, new { messageId = IdOf(second), reason = "spam" });

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(id, (await again.JsonAsync()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        Assert.NotEqual(id, (await other.JsonAsync()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Reports_are_rate_limited_per_user()
    {
        var owner = await api.CreatePersonAsync();
        var reporter = await api.CreatePersonAsync();
        var group = await api.CreateGroupAsync(owner, reporter);
        await using var ownerHub = await api.ConnectAsync(owner);
        var ids = new List<Guid>();
        for (var i = 0; i < 11; i++)
        {
            ids.Add(IdOf(await SendMessageTests.SendAsync(ownerHub, group, $"m{i}")));
            api.Time.Advance(TimeSpan.FromMilliseconds(600)); // keep clear of the 20-per-10-seconds send limit
        }

        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.Created, (await api.ReportAsync(reporter, new { messageId = ids[i], reason = "spam" })).StatusCode);

        await (await api.ReportAsync(reporter, new { messageId = ids[10], reason = "spam" })).AssertErrorAsync((HttpStatusCode)429, "rate_limited");
    }

    // --- users ---

    [Fact]
    public async Task A_user_can_be_reported_without_a_message()
    {
        var reporter = await api.CreatePersonAsync();
        var target = await api.CreatePersonAsync("Trouble Maker");

        var response = await api.ReportAsync(reporter, new { targetUserId = target.Id, reason = "other", details = "impersonation" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var report = await api.WithDbAsync(db => db.Reports.AsNoTracking().SingleAsync(r => r.Id == response.JsonAsync().Result.GetProperty("id").GetGuid()));
        Assert.Equal(target.Id, report.TargetUserId);
        Assert.Null(report.MessageId);
        var evidence = JsonDocument.Parse(report.Evidence).RootElement;
        Assert.Equal("user", evidence.GetProperty("kind").GetString());
        Assert.Equal("Trouble Maker", evidence.GetProperty("user").GetProperty("displayName").GetString());
        Assert.Equal(HttpStatusCode.OK, (await api.ReportAsync(reporter, new { targetUserId = target.Id, reason = "spam" })).StatusCode); // same user again
    }

    [Fact]
    public async Task A_user_who_blocked_you_looks_missing_and_you_cannot_report_yourself()
    {
        var reporter = await api.CreatePersonAsync();
        var blocker = await api.CreatePersonAsync();
        (await api.Client.PutAsync(blocker, $"/blocks/{reporter.Id}")).EnsureSuccessStatusCode();

        await (await api.ReportAsync(reporter, new { targetUserId = blocker.Id, reason = "spam" })).AssertErrorAsync(HttpStatusCode.NotFound, "user_not_found");
        await (await api.ReportAsync(reporter, new { targetUserId = Guid.NewGuid(), reason = "spam" })).AssertErrorAsync(HttpStatusCode.NotFound, "user_not_found");
        await (await api.ReportAsync(reporter, new { targetUserId = reporter.Id, reason = "spam" })).AssertErrorAsync(HttpStatusCode.BadRequest, "invalid_target");
    }

    [Fact]
    public async Task Bad_requests_are_validation_failures()
    {
        var reporter = await api.CreatePersonAsync();
        var target = await api.CreatePersonAsync();

        await (await api.ReportAsync(reporter, new { targetUserId = target.Id, reason = "rude" })).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
        await (await api.ReportAsync(reporter, new { targetUserId = target.Id })).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
        await (await api.ReportAsync(reporter, new { targetUserId = target.Id, reason = "spam", details = new string('d', 1001) })).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
        await (await api.ReportAsync(reporter, new { reason = "spam" })).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Reporting_needs_a_session()
    {
        var response = await api.NewClient().PostAsJsonAsyncCompat("/reports", new { targetUserId = Guid.NewGuid(), reason = "spam" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

internal static class HttpCompat
{
    public static Task<HttpResponseMessage> PostAsJsonAsyncCompat(this HttpClient client, string url, object body) =>
        System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client, url, body);
}

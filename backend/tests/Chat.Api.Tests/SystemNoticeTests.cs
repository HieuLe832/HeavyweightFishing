using System.Net;
using System.Text.Json;
using Chat.Data;
using Chat.Data.Entities;
using Microsoft.EntityFrameworkCore;
using static Chat.Api.Tests.HubTestHelpers;
using static Chat.Api.Tests.SendMessageTests;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class SystemNoticeTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private HttpClient C => api.Client;

    private Task<List<Message>> MessagesAsync(Guid chat) =>
        api.WithDbAsync(db => db.Messages.AsNoTracking().Where(m => m.ConversationId == chat).OrderBy(m => m.Seq).ToListAsync());

    private static JsonElement Body(Message m) => JsonDocument.Parse(m.Body!).RootElement;

    [Fact]
    public async Task Rename_stores_one_system_message_and_members_receive_it()
    {
        var owner = await api.CreatePersonAsync();
        var member = await api.CreatePersonAsync();
        var chat = await api.CreateGroupAsync(owner, member);
        await using var hub = await api.ConnectAsync(member);
        var inbox = Inbox.Listen(hub);

        (await C.PatchAsync(owner, $"/conversations/{chat}", new { title = " Fishers " })).EnsureSuccessStatusCode();

        var stored = Assert.Single(await MessagesAsync(chat));
        Assert.Equal((MessageType.System, 1L), (stored.Type, stored.Seq));
        Assert.Null(stored.SenderId);
        Assert.Equal("renamed", Body(stored).GetProperty("kind").GetString());
        Assert.Equal("Fishers", Body(stored).GetProperty("title").GetString());
        Assert.Equal(owner.Id, Body(stored).GetProperty("actorId").GetGuid());
        var seen = await inbox.Next("message.created");
        Assert.Equal(stored.Id, seen.GetProperty("id").GetGuid());
        Assert.Equal("system", seen.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("senderId").ValueKind);
    }

    [Fact]
    public async Task Adding_members_stores_one_notice_per_new_member_and_none_for_current_ones()
    {
        var owner = await api.CreatePersonAsync();
        var existing = await api.CreatePersonAsync();
        var a = await api.CreatePersonAsync();
        var b = await api.CreatePersonAsync();
        var chat = await api.CreateGroupAsync(owner, existing);

        (await C.PostAsync(owner, $"/conversations/{chat}/members", new { userIds = new[] { a.Id, b.Id } })).EnsureSuccessStatusCode();
        (await C.PostAsync(owner, $"/conversations/{chat}/members", new { userIds = new[] { existing.Id } })).EnsureSuccessStatusCode();

        var notices = await MessagesAsync(chat);
        Assert.Equal(2, notices.Count);
        Assert.All(notices, n => Assert.Equal("member_added", Body(n).GetProperty("kind").GetString()));
        Assert.Equal(new[] { a.Id, b.Id }.Order(), notices.Select(n => Body(n).GetProperty("userId").GetGuid()).Order());
        Assert.All(notices, n => Assert.Equal(owner.Id, Body(n).GetProperty("actorId").GetGuid()));
    }

    [Fact]
    public async Task Removing_a_member_notifies_them_and_later_messages_do_not_reach_them()
    {
        var owner = await api.CreatePersonAsync();
        var member = await api.CreatePersonAsync();
        var chat = await api.CreateGroupAsync(owner, member);
        await using var memberHub = await api.ConnectAsync(member);
        await using var ownerHub = await api.ConnectAsync(owner);
        var inbox = Inbox.Listen(memberHub);

        (await C.DeleteAsync(owner, $"/conversations/{chat}/members/{member.Id}")).EnsureSuccessStatusCode();

        var notice = await inbox.Next("message.created");
        Assert.Equal("member_removed", JsonDocument.Parse(notice.GetProperty("body").GetString()!).RootElement.GetProperty("kind").GetString());
        await SendAsync(ownerHub, chat, "after you left");
        await inbox.ExpectNone("message.created");
    }

    [Fact]
    public async Task Leaving_stores_member_left_without_an_actor_and_the_leaver_receives_it()
    {
        var owner = await api.CreatePersonAsync();
        var member = await api.CreatePersonAsync();
        var chat = await api.CreateGroupAsync(owner, member);
        await using var memberHub = await api.ConnectAsync(member);
        var inbox = Inbox.Listen(memberHub);

        (await C.PostAsync(member, $"/conversations/{chat}/leave")).EnsureSuccessStatusCode();

        var stored = Assert.Single(await MessagesAsync(chat));
        Assert.Equal("member_left", Body(stored).GetProperty("kind").GetString());
        Assert.Equal(member.Id, Body(stored).GetProperty("userId").GetGuid());
        Assert.False(Body(stored).TryGetProperty("actorId", out _));
        Assert.Equal(stored.Id, (await inbox.Next("message.created")).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_role_change_stores_nothing()
    {
        var owner = await api.CreatePersonAsync();
        var member = await api.CreatePersonAsync();
        var chat = await api.CreateGroupAsync(owner, member);

        (await C.PutAsync(owner, $"/conversations/{chat}/members/{member.Id}/role", new { role = "admin" })).EnsureSuccessStatusCode();

        Assert.Empty(await MessagesAsync(chat));
    }

    [Fact]
    public async Task A_refused_change_stores_nothing_and_keeps_last_seq()
    {
        var owner = await api.CreatePersonAsync();
        var member = await api.CreatePersonAsync();
        var chat = await api.CreateGroupAsync(owner, member);

        await (await C.PatchAsync(member, $"/conversations/{chat}", new { title = "Nope" })).AssertErrorAsync(HttpStatusCode.Forbidden, "insufficient_role");

        Assert.Empty(await MessagesAsync(chat));
        Assert.Equal(0, await api.WithDbAsync(db => db.Conversations.Where(c => c.Id == chat).Select(c => c.LastSeq).SingleAsync()));
    }

    [Fact]
    public async Task Notices_and_user_messages_share_one_gapless_sequence()
    {
        var owner = await api.CreatePersonAsync();
        var member = await api.CreatePersonAsync();
        var chat = await api.CreateGroupAsync(owner, member);
        await using var hub = await api.ConnectAsync(member);

        var sends = Enumerable.Range(0, 5).Select(i => (Task)SendAsync(hub, chat, $"m{i}"));
        var renames = Enumerable.Range(0, 5).Select(async i => (await C.PatchAsync(owner, $"/conversations/{chat}", new { title = $"t{i}" })).EnsureSuccessStatusCode());
        await Task.WhenAll(sends.Concat(renames));

        var all = await MessagesAsync(chat);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), all.Select(m => m.Seq));
        Assert.Equal(5, all.Count(m => m.Type == MessageType.System));
    }
}

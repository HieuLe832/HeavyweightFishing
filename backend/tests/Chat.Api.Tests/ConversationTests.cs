using System.Net;
using System.Text.Json;
using Chat.Api.Conversations;
using Chat.Data;
using Microsoft.EntityFrameworkCore;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class ConversationTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private HttpClient C => api.Client;

    private async Task<Guid> OpenDirectAsync(Person me, Person other)
    {
        var response = await C.PostAsync(me, "/conversations/direct", new { userId = other.Id });
        Assert.True(response.IsSuccessStatusCode);
        return (await response.JsonAsync()).GetProperty("conversation").GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateGroupAsync(Person owner, string title, params Person[] members)
    {
        var response = await C.PostAsync(owner, "/conversations/group", new { title, memberIds = members.Select(m => m.Id) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("conversation").GetProperty("id").GetGuid();
    }

    private static List<Guid> Ids(JsonElement list) => list.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();

    [Fact]
    public async Task Endpoints_require_a_token()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await C.GetAsync("/conversations")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await C.GetAsync($"/conversations/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Direct_chat_is_created_once_and_found_again_from_either_side()
    {
        var lan = await api.CreatePersonAsync("Lan");
        var minh = await api.CreatePersonAsync("Minh");

        var first = await C.PostAsync(lan, "/conversations/direct", new { userId = minh.Id });
        var again = await C.PostAsync(lan, "/conversations/direct", new { userId = minh.Id });
        var reverse = await C.PostAsync(minh, "/conversations/direct", new { userId = lan.Id });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reverse.StatusCode);
        var body = await first.JsonAsync();
        var conversation = body.GetProperty("conversation");
        Assert.Equal("direct", conversation.GetProperty("type").GetString());
        Assert.Equal("member", conversation.GetProperty("role").GetString());
        Assert.Equal(minh.Id, conversation.GetProperty("peer").GetProperty("id").GetGuid());
        Assert.Equal("Minh", conversation.GetProperty("peer").GetProperty("displayName").GetString());
        Assert.Equal(2, body.GetProperty("members").GetArrayLength());
        var id = conversation.GetProperty("id").GetGuid();
        Assert.Equal(id, (await again.JsonAsync()).GetProperty("conversation").GetProperty("id").GetGuid());
        var reversed = (await reverse.JsonAsync()).GetProperty("conversation");
        Assert.Equal(id, reversed.GetProperty("id").GetGuid());
        Assert.Equal(lan.Id, reversed.GetProperty("peer").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Direct_chat_key_is_the_sorted_pair()
    {
        var a = await api.CreatePersonAsync();
        var b = await api.CreatePersonAsync();
        var id = await OpenDirectAsync(a, b);

        var key = await api.WithDbAsync(db => db.Conversations.Where(c => c.Id == id).Select(c => c.DirectKey).SingleAsync());

        Assert.Equal(ConversationService.DirectKey(b.Id, a.Id), key);
        Assert.Equal(ConversationService.DirectKey(a.Id, b.Id), key);
    }

    [Fact]
    public async Task Parallel_direct_chats_from_both_sides_end_up_as_one_conversation()
    {
        var a = await api.CreatePersonAsync();
        var b = await api.CreatePersonAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            i % 2 == 0 ? C.PostAsync(a, "/conversations/direct", new { userId = b.Id })
                       : C.PostAsync(b, "/conversations/direct", new { userId = a.Id })));

        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode));
        var ids = new HashSet<Guid>();
        foreach (var r in responses) ids.Add((await r.JsonAsync()).GetProperty("conversation").GetProperty("id").GetGuid());
        Assert.Single(ids);
    }

    [Fact]
    public async Task Direct_chat_rejects_yourself_unknown_users_and_a_missing_user_id()
    {
        var me = await api.CreatePersonAsync();

        await (await C.PostAsync(me, "/conversations/direct", new { userId = me.Id })).AssertErrorAsync(HttpStatusCode.BadRequest, "invalid_target");
        await (await C.PostAsync(me, "/conversations/direct", new { userId = Guid.NewGuid() })).AssertErrorAsync(HttpStatusCode.NotFound, "user_not_found");
        await (await C.PostAsync(me, "/conversations/direct", new { })).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Direct_chat_respects_blocks_in_both_directions()
    {
        var me = await api.CreatePersonAsync();
        var iBlocked = await api.CreatePersonAsync();
        var blockedMe = await api.CreatePersonAsync();
        (await C.PutAsync(me, $"/blocks/{iBlocked.Id}")).EnsureSuccessStatusCode();
        (await C.PutAsync(blockedMe, $"/blocks/{me.Id}")).EnsureSuccessStatusCode();

        await (await C.PostAsync(me, "/conversations/direct", new { userId = iBlocked.Id })).AssertErrorAsync(HttpStatusCode.Forbidden, "blocked");
        await (await C.PostAsync(me, "/conversations/direct", new { userId = blockedMe.Id })).AssertErrorAsync(HttpStatusCode.NotFound, "user_not_found");
    }

    [Fact]
    public async Task Group_is_created_with_the_creator_as_owner()
    {
        var owner = await api.CreatePersonAsync("Owner");
        var a = await api.CreatePersonAsync();
        var b = await api.CreatePersonAsync();

        var response = await C.PostAsync(owner, "/conversations/group",
            new { title = "  Câu cá cuối tuần  ", memberIds = new[] { a.Id, b.Id, a.Id } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.JsonAsync();
        var conversation = body.GetProperty("conversation");
        Assert.Equal("group", conversation.GetProperty("type").GetString());
        Assert.Equal("Câu cá cuối tuần", conversation.GetProperty("title").GetString());
        Assert.Equal("owner", conversation.GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, conversation.GetProperty("peer").ValueKind);
        var roles = body.GetProperty("members").EnumerateArray()
            .ToDictionary(m => m.GetProperty("userId").GetGuid(), m => m.GetProperty("role").GetString());
        Assert.Equal(3, roles.Count);
        Assert.Equal("owner", roles[owner.Id]);
        Assert.Equal("member", roles[a.Id]);
        var id = conversation.GetProperty("id").GetGuid();
        var key = await api.WithDbAsync(db => db.Conversations.Where(c => c.Id == id).Select(c => c.DirectKey).SingleAsync());
        Assert.Null(key);
    }

    [Fact]
    public async Task Group_validates_title_and_members()
    {
        var owner = await api.CreatePersonAsync();
        var a = await api.CreatePersonAsync();

        foreach (var body in new object[]
        {
            new { title = "", memberIds = new[] { a.Id } },
            new { title = new string('x', 101), memberIds = new[] { a.Id } },
            new { title = "ok", memberIds = Array.Empty<Guid>() },
            new { title = "ok" },
        })
            await (await C.PostAsync(owner, "/conversations/group", body)).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Group_rejects_yourself_unknown_banned_and_blocking_members_and_creates_nothing()
    {
        var owner = await api.CreatePersonAsync();
        var ok = await api.CreatePersonAsync();
        var banned = await api.CreatePersonAsync();
        var blockedMe = await api.CreatePersonAsync();
        var iBlocked = await api.CreatePersonAsync();
        await api.WithDbAsync(db => db.Users.Where(u => u.Id == banned.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Banned)));
        (await C.PutAsync(blockedMe, $"/blocks/{owner.Id}")).EnsureSuccessStatusCode();
        (await C.PutAsync(owner, $"/blocks/{iBlocked.Id}")).EnsureSuccessStatusCode();

        async Task<HttpResponseMessage> Try(params Guid[] ids) =>
            await C.PostAsync(owner, "/conversations/group", new { title = "g", memberIds = ids });

        await (await Try(ok.Id, owner.Id)).AssertErrorAsync(HttpStatusCode.BadRequest, "invalid_target");
        await (await Try(ok.Id, Guid.NewGuid())).AssertErrorAsync(HttpStatusCode.NotFound, "user_not_found");
        await (await Try(ok.Id, banned.Id)).AssertErrorAsync(HttpStatusCode.NotFound, "user_not_found");
        await (await Try(ok.Id, blockedMe.Id)).AssertErrorAsync(HttpStatusCode.NotFound, "user_not_found");
        await (await Try(ok.Id, iBlocked.Id)).AssertErrorAsync(HttpStatusCode.Forbidden, "blocked");
        Assert.Empty((await (await C.GetAsync(owner, "/conversations")).JsonAsync()).EnumerateArray());
    }

    [Fact]
    public async Task Get_returns_members_to_a_member_and_hides_the_conversation_from_everyone_else()
    {
        var owner = await api.CreatePersonAsync();
        var member = await api.CreatePersonAsync();
        var outsider = await api.CreatePersonAsync();
        var id = await CreateGroupAsync(owner, "g", member);

        var seen = await C.GetAsync(member, $"/conversations/{id}");

        Assert.Equal(HttpStatusCode.OK, seen.StatusCode);
        Assert.Equal(2, (await seen.JsonAsync()).GetProperty("members").GetArrayLength());
        await (await C.GetAsync(outsider, $"/conversations/{id}")).AssertErrorAsync(HttpStatusCode.NotFound, "conversation_not_found");
        await (await C.GetAsync(owner, $"/conversations/{Guid.NewGuid()}")).AssertErrorAsync(HttpStatusCode.NotFound, "conversation_not_found");
    }

    [Fact]
    public async Task List_returns_my_conversations_newest_activity_first_with_unread_counts()
    {
        var me = await api.CreatePersonAsync();
        var a = await api.CreatePersonAsync();
        var b = await api.CreatePersonAsync();
        var stranger = await api.CreatePersonAsync();
        var direct = await OpenDirectAsync(me, a);
        api.Time.Advance(TimeSpan.FromMinutes(1));
        var group = await CreateGroupAsync(me, "g", b);
        await CreateGroupAsync(stranger, "not mine", a);
        await api.WithDbAsync(db => db.Conversations.Where(c => c.Id == direct)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastSeq, 5L)));
        await api.WithDbAsync(db => db.ConversationMembers.Where(m => m.ConversationId == direct && m.UserId == me.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastReadSeq, 2L)));

        var body = await (await C.GetAsync(me, "/conversations")).JsonAsync();

        Assert.Equal([group, direct], Ids(body));
        var directRow = body.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == direct);
        Assert.Equal(5, directRow.GetProperty("lastSeq").GetInt64());
        Assert.Equal(3, directRow.GetProperty("unread").GetInt64());
        Assert.Equal(a.Id, directRow.GetProperty("peer").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task List_pages_with_limit_and_before()
    {
        var me = await api.CreatePersonAsync();
        var a = await api.CreatePersonAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            api.Time.Advance(TimeSpan.FromMinutes(1));
            ids.Add(await CreateGroupAsync(me, $"g{i}", a));
        }

        var page1 = await (await C.GetAsync(me, "/conversations?limit=2")).JsonAsync();
        var last = page1.EnumerateArray().Last().GetProperty("updatedAt").GetString()!;
        var page2 = await (await C.GetAsync(me, $"/conversations?limit=2&before={Uri.EscapeDataString(last)}")).JsonAsync();

        Assert.Equal([ids[2], ids[1]], Ids(page1));
        Assert.Equal([ids[0]], Ids(page2));
        await (await C.GetAsync(me, "/conversations?limit=0")).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Mute_sets_and_clears_the_time_for_this_member_only()
    {
        var me = await api.CreatePersonAsync();
        var other = await api.CreatePersonAsync();
        var id = await OpenDirectAsync(me, other);
        var until = api.Time.GetUtcNow().AddHours(8);

        Assert.Equal(HttpStatusCode.NoContent, (await C.PutAsync(me, $"/conversations/{id}/mute", new { until })).StatusCode);

        var mine = (await (await C.GetAsync(me, $"/conversations/{id}")).JsonAsync()).GetProperty("conversation");
        Assert.Equal(until, mine.GetProperty("mutedUntil").GetDateTimeOffset());
        var theirs = (await (await C.GetAsync(other, $"/conversations/{id}")).JsonAsync()).GetProperty("conversation");
        Assert.Equal(JsonValueKind.Null, theirs.GetProperty("mutedUntil").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await C.DeleteAsync(me, $"/conversations/{id}/mute")).StatusCode);
        var cleared = (await (await C.GetAsync(me, $"/conversations/{id}")).JsonAsync()).GetProperty("conversation");
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("mutedUntil").ValueKind);
    }

    [Fact]
    public async Task Mute_needs_a_future_time_and_a_conversation_you_are_in()
    {
        var me = await api.CreatePersonAsync();
        var a = await api.CreatePersonAsync();
        var outsider = await api.CreatePersonAsync();
        var id = await CreateGroupAsync(me, "g", a);

        await (await C.PutAsync(me, $"/conversations/{id}/mute", new { until = api.Time.GetUtcNow().AddSeconds(-1) }))
            .AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
        await (await C.PutAsync(me, $"/conversations/{id}/mute", new { }))
            .AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
        await (await C.PutAsync(outsider, $"/conversations/{id}/mute", new { until = api.Time.GetUtcNow().AddHours(1) }))
            .AssertErrorAsync(HttpStatusCode.NotFound, "conversation_not_found");
    }
}

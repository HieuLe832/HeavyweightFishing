using System.Net;
using Chat.Data;
using Microsoft.EntityFrameworkCore;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class ContactTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private HttpClient C => api.Client;

    private static async Task<List<Guid>> IdsAsync(HttpResponseMessage response, string property = "userId") =>
        (await response.JsonAsync()).EnumerateArray().Select(e => e.GetProperty(property).GetGuid()).ToList();

    [Fact]
    public async Task Endpoints_require_a_token()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await C.GetAsync("/contacts")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await C.GetAsync("/blocks")).StatusCode);
    }

    [Fact]
    public async Task Add_contact_stores_it_and_lists_it()
    {
        var me = await api.CreatePersonAsync("Lan");
        var friend = await api.CreatePersonAsync("Minh");

        var put = await C.PutAsync(me, $"/contacts/{friend.Id}");

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var body = await put.JsonAsync();
        Assert.Equal(friend.Id, body.GetProperty("userId").GetGuid());
        Assert.Equal("Minh", body.GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKindNull, body.GetProperty("nickname").ValueKind);
        Assert.Equal([friend.Id], await IdsAsync(await C.GetAsync(me, "/contacts")));
        // It is a private list: the friend has not gained a contact.
        Assert.Empty(await IdsAsync(await C.GetAsync(friend, "/contacts")));
    }

    private const System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;

    [Fact]
    public async Task Add_contact_twice_updates_the_nickname_and_keeps_one_row()
    {
        var me = await api.CreatePersonAsync();
        var friend = await api.CreatePersonAsync();

        (await C.PutAsync(me, $"/contacts/{friend.Id}", new { nickname = "Old" })).EnsureSuccessStatusCode();
        var second = await C.PutAsync(me, $"/contacts/{friend.Id}", new { nickname = "  Cá Mập  " });

        Assert.Equal("Cá Mập", (await second.JsonAsync()).GetProperty("nickname").GetString());
        Assert.Single(await IdsAsync(await C.GetAsync(me, "/contacts")));
    }

    [Fact]
    public async Task Add_contact_without_a_body_clears_the_nickname()
    {
        var me = await api.CreatePersonAsync();
        var friend = await api.CreatePersonAsync();
        (await C.PutAsync(me, $"/contacts/{friend.Id}", new { nickname = "Old" })).EnsureSuccessStatusCode();

        var response = await C.PutAsync(me, $"/contacts/{friend.Id}");

        Assert.Equal(JsonValueKindNull, (await response.JsonAsync()).GetProperty("nickname").ValueKind);
    }

    [Fact]
    public async Task Add_contact_rejects_a_blank_or_long_nickname()
    {
        var me = await api.CreatePersonAsync();
        var friend = await api.CreatePersonAsync();

        await (await C.PutAsync(me, $"/contacts/{friend.Id}", new { nickname = "  " }))
            .AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
        await (await C.PutAsync(me, $"/contacts/{friend.Id}", new { nickname = new string('a', 65) }))
            .AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Add_contact_rejects_yourself_and_unknown_users()
    {
        var me = await api.CreatePersonAsync();

        await (await C.PutAsync(me, $"/contacts/{me.Id}")).AssertErrorAsync(HttpStatusCode.BadRequest, "invalid_target");
        await (await C.PutAsync(me, $"/contacts/{Guid.NewGuid()}")).AssertErrorAsync(HttpStatusCode.NotFound, "user_not_found");
    }

    [Fact]
    public async Task Add_contact_rejects_someone_who_blocked_you_without_saying_so()
    {
        var me = await api.CreatePersonAsync();
        var blocker = await api.CreatePersonAsync();
        (await C.PutAsync(blocker, $"/blocks/{me.Id}")).EnsureSuccessStatusCode();

        await (await C.PutAsync(me, $"/contacts/{blocker.Id}")).AssertErrorAsync(HttpStatusCode.NotFound, "user_not_found");
    }

    [Fact]
    public async Task Add_contact_rejects_someone_you_blocked()
    {
        var me = await api.CreatePersonAsync();
        var other = await api.CreatePersonAsync();
        (await C.PutAsync(me, $"/blocks/{other.Id}")).EnsureSuccessStatusCode();

        await (await C.PutAsync(me, $"/contacts/{other.Id}")).AssertErrorAsync(HttpStatusCode.Forbidden, "blocked");
    }

    [Fact]
    public async Task Remove_contact_is_idempotent()
    {
        var me = await api.CreatePersonAsync();
        var friend = await api.CreatePersonAsync();
        (await C.PutAsync(me, $"/contacts/{friend.Id}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await C.DeleteAsync(me, $"/contacts/{friend.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await C.DeleteAsync(me, $"/contacts/{friend.Id}")).StatusCode);
        Assert.Empty(await IdsAsync(await C.GetAsync(me, "/contacts")));
    }

    [Fact]
    public async Task List_orders_by_nickname_or_display_name_ignoring_case_and_hides_banned_and_blocking_users()
    {
        var me = await api.CreatePersonAsync();
        var bee = await api.CreatePersonAsync("bee");
        var ann = await api.CreatePersonAsync("Zed");
        var banned = await api.CreatePersonAsync("Banned");
        var blocker = await api.CreatePersonAsync("Blocker");
        foreach (var p in new[] { bee, ann, banned, blocker }) (await C.PutAsync(me, $"/contacts/{p.Id}")).EnsureSuccessStatusCode();
        (await C.PutAsync(me, $"/contacts/{ann.Id}", new { nickname = "Ace" })).EnsureSuccessStatusCode();
        await api.WithDbAsync(db => db.Users.Where(u => u.Id == banned.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Banned)));
        (await C.PutAsync(blocker, $"/blocks/{me.Id}")).EnsureSuccessStatusCode();

        Assert.Equal([ann.Id, bee.Id], await IdsAsync(await C.GetAsync(me, "/contacts")));
    }

    [Fact]
    public async Task Block_stores_the_block_removes_the_contact_and_lists_it()
    {
        var me = await api.CreatePersonAsync();
        var other = await api.CreatePersonAsync();
        (await C.PutAsync(me, $"/contacts/{other.Id}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await C.PutAsync(me, $"/blocks/{other.Id}")).StatusCode);

        Assert.Empty(await IdsAsync(await C.GetAsync(me, "/contacts")));
        Assert.Equal([other.Id], await IdsAsync(await C.GetAsync(me, "/blocks")));
        // The block is one-way.
        Assert.Empty(await IdsAsync(await C.GetAsync(other, "/blocks")));
    }

    [Fact]
    public async Task Block_twice_is_fine_and_keeps_one_row()
    {
        var me = await api.CreatePersonAsync();
        var other = await api.CreatePersonAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await C.PutAsync(me, $"/blocks/{other.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await C.PutAsync(me, $"/blocks/{other.Id}")).StatusCode);

        Assert.Single(await IdsAsync(await C.GetAsync(me, "/blocks")));
    }

    [Fact]
    public async Task Block_rejects_yourself_and_unknown_users()
    {
        var me = await api.CreatePersonAsync();

        await (await C.PutAsync(me, $"/blocks/{me.Id}")).AssertErrorAsync(HttpStatusCode.BadRequest, "invalid_target");
        await (await C.PutAsync(me, $"/blocks/{Guid.NewGuid()}")).AssertErrorAsync(HttpStatusCode.NotFound, "user_not_found");
    }

    [Fact]
    public async Task Unblock_removes_the_block_and_is_idempotent()
    {
        var me = await api.CreatePersonAsync();
        var other = await api.CreatePersonAsync();
        (await C.PutAsync(me, $"/blocks/{other.Id}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await C.DeleteAsync(me, $"/blocks/{other.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await C.DeleteAsync(me, $"/blocks/{other.Id}")).StatusCode);

        Assert.Empty(await IdsAsync(await C.GetAsync(me, "/blocks")));
        Assert.Equal(HttpStatusCode.OK, (await C.PutAsync(me, $"/contacts/{other.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_banned_user_you_blocked_can_still_be_unblocked()
    {
        var me = await api.CreatePersonAsync();
        var other = await api.CreatePersonAsync();
        (await C.PutAsync(me, $"/blocks/{other.Id}")).EnsureSuccessStatusCode();
        await api.WithDbAsync(db => db.Users.Where(u => u.Id == other.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Banned)));

        Assert.Equal([other.Id], await IdsAsync(await C.GetAsync(me, "/blocks")));
        Assert.Equal(HttpStatusCode.NoContent, (await C.DeleteAsync(me, $"/blocks/{other.Id}")).StatusCode);
    }
}

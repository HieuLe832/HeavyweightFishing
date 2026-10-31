using System.Net;
using Microsoft.EntityFrameworkCore;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class ProfileTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private HttpClient C => api.Client;

    [Fact]
    public async Task Endpoints_require_a_token()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await C.SendAsync(new HttpRequestMessage(HttpMethod.Patch, "/users/me"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await C.GetAsync("/users/search?q=ab")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await C.GetAsync($"/users/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Update_profile_trims_and_stores_the_display_name()
    {
        var me = await api.CreatePersonAsync();

        var response = await C.PatchAsync(me, "/users/me", new { displayName = "  Lan Anh  " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Lan Anh", (await response.JsonAsync()).GetProperty("displayName").GetString());
        var stored = await api.WithDbAsync(db => db.Users.Where(u => u.Id == me.Id).Select(u => u.DisplayName).SingleAsync());
        Assert.Equal("Lan Anh", stored);
        var fromMe = await (await C.GetAsync(me, "/auth/me")).JsonAsync();
        Assert.Equal("Lan Anh", fromMe.GetProperty("displayName").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Update_profile_rejects_a_blank_display_name(string? name)
    {
        var me = await api.CreatePersonAsync();

        await (await C.PatchAsync(me, "/users/me", new { displayName = name }))
            .AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Update_profile_rejects_a_display_name_over_64_characters()
    {
        var me = await api.CreatePersonAsync();

        await (await C.PatchAsync(me, "/users/me", new { displayName = new string('a', 65) }))
            .AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Get_user_returns_a_public_profile_without_the_email()
    {
        var me = await api.CreatePersonAsync();
        var other = await api.CreatePersonAsync("Minh");

        var response = await C.GetAsync(me, $"/users/{other.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal(other.Username, body.GetProperty("username").GetString());
        Assert.Equal("Minh", body.GetProperty("displayName").GetString());
        Assert.False(body.GetProperty("isContact").GetBoolean());
        Assert.False(body.GetProperty("isBlocked").GetBoolean());
        Assert.False(body.TryGetProperty("email", out _));
    }

    [Fact]
    public async Task Get_user_can_return_yourself()
    {
        var me = await api.CreatePersonAsync();

        Assert.Equal(HttpStatusCode.OK, (await C.GetAsync(me, $"/users/{me.Id}")).StatusCode);
    }

    [Fact]
    public async Task Get_user_flags_contacts_and_blocks()
    {
        var me = await api.CreatePersonAsync();
        var friend = await api.CreatePersonAsync();
        var enemy = await api.CreatePersonAsync();
        (await C.PutAsync(me, $"/contacts/{friend.Id}")).EnsureSuccessStatusCode();
        (await C.PutAsync(me, $"/blocks/{enemy.Id}")).EnsureSuccessStatusCode();

        Assert.True((await (await C.GetAsync(me, $"/users/{friend.Id}")).JsonAsync()).GetProperty("isContact").GetBoolean());
        Assert.True((await (await C.GetAsync(me, $"/users/{enemy.Id}")).JsonAsync()).GetProperty("isBlocked").GetBoolean());
    }

    [Fact]
    public async Task Get_user_hides_unknown_banned_and_blocking_users_the_same_way()
    {
        var me = await api.CreatePersonAsync();
        var banned = await api.CreatePersonAsync();
        var blocker = await api.CreatePersonAsync();
        await api.WithDbAsync(db => db.Users.Where(u => u.Id == banned.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, Chat.Data.UserStatus.Banned)));
        (await C.PutAsync(blocker, $"/blocks/{me.Id}")).EnsureSuccessStatusCode();

        foreach (var id in new[] { Guid.NewGuid(), banned.Id, blocker.Id })
            await (await C.GetAsync(me, $"/users/{id}")).AssertErrorAsync(HttpStatusCode.NotFound, "user_not_found");
    }

    [Fact]
    public async Task Search_matches_username_prefix_and_display_name_substring_case_insensitively()
    {
        var me = await api.CreatePersonAsync();
        var tag = Unique("zq");
        var byName = await api.CreatePersonAsync($"Big {tag.ToUpperInvariant()} Fish");
        var byPrefix = await RegisterWithUsernameAsync(tag + "_angler");
        var unrelated = await api.CreatePersonAsync();

        var body = await (await C.GetAsync(me, $"/users/search?q={tag}")).JsonAsync();

        var ids = body.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(byName.Id, ids);
        Assert.Contains(byPrefix.Id, ids);
        Assert.DoesNotContain(unrelated.Id, ids);
    }

    [Fact]
    public async Task Search_does_not_treat_wildcards_as_wildcards()
    {
        var me = await api.CreatePersonAsync();
        await api.CreatePersonAsync();

        var body = await (await C.GetAsync(me, "/users/search?q=%25%25")).JsonAsync();

        Assert.Empty(body.EnumerateArray());
    }

    [Fact]
    public async Task Search_leaves_out_yourself_banned_pending_and_users_who_blocked_you()
    {
        var me = await api.CreatePersonAsync();
        var tag = Unique("zr");
        var visible = await RegisterWithUsernameAsync(tag + "_a");
        var banned = await RegisterWithUsernameAsync(tag + "_b");
        var blocker = await RegisterWithUsernameAsync(tag + "_c");
        var self = await RegisterWithUsernameAsync(tag + "_d");
        (await C.RegisterAsync(tag + "_e", tag + "_e@example.com")).EnsureSuccessStatusCode(); // never verified
        await api.WithDbAsync(db => db.Users.Where(u => u.Id == banned.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, Chat.Data.UserStatus.Banned)));
        (await C.PutAsync(blocker, $"/blocks/{self.Id}")).EnsureSuccessStatusCode();

        var ids = (await (await C.GetAsync(self, $"/users/search?q={tag}")).JsonAsync())
            .EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();

        Assert.Equal([visible.Id], ids);
    }

    [Fact]
    public async Task Search_respects_the_limit_and_orders_by_username()
    {
        var me = await api.CreatePersonAsync();
        var tag = Unique("zs");
        await RegisterWithUsernameAsync(tag + "_b");
        await RegisterWithUsernameAsync(tag + "_a");
        await RegisterWithUsernameAsync(tag + "_c");

        var body = await (await C.GetAsync(me, $"/users/search?q={tag}&limit=2")).JsonAsync();

        Assert.Equal([tag + "_a", tag + "_b"], body.EnumerateArray().Select(e => e.GetProperty("username").GetString()));
    }

    [Theory]
    [InlineData("/users/search")]
    [InlineData("/users/search?q=a")]
    [InlineData("/users/search?q=ab&limit=0")]
    [InlineData("/users/search?q=ab&limit=51")]
    public async Task Search_validates_its_parameters(string url)
    {
        var me = await api.CreatePersonAsync();

        await (await C.GetAsync(me, url)).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    private async Task<Person> RegisterWithUsernameAsync(string username)
    {
        var email = username + "@example.com";
        (await C.RegisterAsync(username, email)).EnsureSuccessStatusCode();
        (await C.VerifyEmailAsync(email, api.Email.LastCodeFor(email))).EnsureSuccessStatusCode();
        var body = await (await C.LoginAsync(username)).JsonAsync();
        return new Person(body.GetProperty("user").GetProperty("id").GetGuid(), username,
            body.GetProperty("accessToken").GetString()!);
    }
}

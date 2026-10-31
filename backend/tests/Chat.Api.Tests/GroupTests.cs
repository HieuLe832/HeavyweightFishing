using System.Net;
using System.Text.Json;
using Chat.Data;
using Microsoft.EntityFrameworkCore;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class GroupTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private HttpClient C => api.Client;

    private async Task<Guid> CreateGroupAsync(Person owner, params Person[] members)
    {
        var response = await C.PostAsync(owner, "/conversations/group", new { title = "g", memberIds = members.Select(m => m.Id) });
        response.EnsureSuccessStatusCode();
        return (await response.JsonAsync()).GetProperty("conversation").GetProperty("id").GetGuid();
    }

    private async Task<Dictionary<Guid, string>> RolesAsync(Person viewer, Guid id)
    {
        var body = await (await C.GetAsync(viewer, $"/conversations/{id}")).JsonAsync();
        return body.GetProperty("members").EnumerateArray()
            .ToDictionary(m => m.GetProperty("userId").GetGuid(), m => m.GetProperty("role").GetString()!);
    }

    private Task<HttpResponseMessage> AddAsync(Person who, Guid id, params Person[] users) =>
        C.PostAsync(who, $"/conversations/{id}/members", new { userIds = users.Select(u => u.Id) });

    private Task<HttpResponseMessage> SetRoleAsync(Person who, Guid id, Person target, string role) =>
        C.PutAsync(who, $"/conversations/{id}/members/{target.Id}/role", new { role });

    [Fact]
    public async Task Owner_and_admin_can_rename_but_a_member_cannot()
    {
        var owner = await api.CreatePersonAsync();
        var admin = await api.CreatePersonAsync();
        var member = await api.CreatePersonAsync();
        var id = await CreateGroupAsync(owner, admin, member);
        (await SetRoleAsync(owner, id, admin, "admin")).EnsureSuccessStatusCode();

        var byOwner = await C.PatchAsync(owner, $"/conversations/{id}", new { title = "  New  " });
        var byAdmin = await C.PatchAsync(admin, $"/conversations/{id}", new { title = "Newer" });

        Assert.Equal("New", (await byOwner.JsonAsync()).GetProperty("conversation").GetProperty("title").GetString());
        Assert.Equal("Newer", (await byAdmin.JsonAsync()).GetProperty("conversation").GetProperty("title").GetString());
        await (await C.PatchAsync(member, $"/conversations/{id}", new { title = "No" }))
            .AssertErrorAsync(HttpStatusCode.Forbidden, "insufficient_role");
        await (await C.PatchAsync(owner, $"/conversations/{id}", new { title = " " }))
            .AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Group_changes_move_the_conversation_up_the_list()
    {
        var owner = await api.CreatePersonAsync();
        var a = await api.CreatePersonAsync();
        var id = await CreateGroupAsync(owner, a);
        var before = api.Time.GetUtcNow();
        api.Time.Advance(TimeSpan.FromMinutes(5));

        (await C.PatchAsync(owner, $"/conversations/{id}", new { title = "x" })).EnsureSuccessStatusCode();

        var updated = await api.WithDbAsync(db => db.Conversations.Where(c => c.Id == id).Select(c => c.UpdatedAt).SingleAsync());
        Assert.Equal(before.AddMinutes(5), updated);
    }

    [Fact]
    public async Task Group_actions_are_refused_on_a_direct_chat()
    {
        var a = await api.CreatePersonAsync();
        var b = await api.CreatePersonAsync();
        var c = await api.CreatePersonAsync();
        var id = (await (await C.PostAsync(a, "/conversations/direct", new { userId = b.Id })).JsonAsync())
            .GetProperty("conversation").GetProperty("id").GetGuid();

        await (await C.PatchAsync(a, $"/conversations/{id}", new { title = "x" })).AssertErrorAsync(HttpStatusCode.BadRequest, "not_a_group");
        await (await AddAsync(a, id, c)).AssertErrorAsync(HttpStatusCode.BadRequest, "not_a_group");
        await (await C.DeleteAsync(a, $"/conversations/{id}/members/{b.Id}")).AssertErrorAsync(HttpStatusCode.BadRequest, "not_a_group");
        await (await C.PostAsync(a, $"/conversations/{id}/leave")).AssertErrorAsync(HttpStatusCode.BadRequest, "not_a_group");
        await (await SetRoleAsync(a, id, b, "admin")).AssertErrorAsync(HttpStatusCode.BadRequest, "not_a_group");
    }

    [Fact]
    public async Task Non_members_get_a_not_found_for_every_group_action()
    {
        var owner = await api.CreatePersonAsync();
        var a = await api.CreatePersonAsync();
        var outsider = await api.CreatePersonAsync();
        var id = await CreateGroupAsync(owner, a);

        await (await C.PatchAsync(outsider, $"/conversations/{id}", new { title = "x" })).AssertErrorAsync(HttpStatusCode.NotFound, "conversation_not_found");
        await (await AddAsync(outsider, id, outsider)).AssertErrorAsync(HttpStatusCode.NotFound, "conversation_not_found");
        await (await C.DeleteAsync(outsider, $"/conversations/{id}/members/{a.Id}")).AssertErrorAsync(HttpStatusCode.NotFound, "conversation_not_found");
        await (await C.PostAsync(outsider, $"/conversations/{id}/leave")).AssertErrorAsync(HttpStatusCode.NotFound, "conversation_not_found");
        await (await SetRoleAsync(outsider, id, a, "admin")).AssertErrorAsync(HttpStatusCode.NotFound, "conversation_not_found");
    }

    [Fact]
    public async Task Add_members_adds_new_people_and_ignores_current_members()
    {
        var owner = await api.CreatePersonAsync();
        var a = await api.CreatePersonAsync();
        var b = await api.CreatePersonAsync();
        var id = await CreateGroupAsync(owner, a);

        var response = await AddAsync(owner, id, a, b);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var roles = await RolesAsync(owner, id);
        Assert.Equal(3, roles.Count);
        Assert.Equal("member", roles[b.Id]);
        Assert.Equal("owner", roles[owner.Id]);
        Assert.Equal(HttpStatusCode.OK, (await C.GetAsync(b, $"/conversations/{id}")).StatusCode);
    }

    [Fact]
    public async Task Add_members_needs_a_manager_and_reachable_users()
    {
        var owner = await api.CreatePersonAsync();
        var member = await api.CreatePersonAsync();
        var newcomer = await api.CreatePersonAsync();
        var blockedMe = await api.CreatePersonAsync();
        var iBlocked = await api.CreatePersonAsync();
        var id = await CreateGroupAsync(owner, member);
        (await C.PutAsync(blockedMe, $"/blocks/{owner.Id}")).EnsureSuccessStatusCode();
        (await C.PutAsync(owner, $"/blocks/{iBlocked.Id}")).EnsureSuccessStatusCode();

        await (await AddAsync(member, id, newcomer)).AssertErrorAsync(HttpStatusCode.Forbidden, "insufficient_role");
        await (await AddAsync(owner, id, new Person(Guid.NewGuid(), "", ""))).AssertErrorAsync(HttpStatusCode.NotFound, "user_not_found");
        await (await AddAsync(owner, id, blockedMe)).AssertErrorAsync(HttpStatusCode.NotFound, "user_not_found");
        await (await AddAsync(owner, id, iBlocked)).AssertErrorAsync(HttpStatusCode.Forbidden, "blocked");
        await (await C.PostAsync(owner, $"/conversations/{id}/members", new { userIds = Array.Empty<Guid>() }))
            .AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(2, (await RolesAsync(owner, id)).Count);
    }

    [Fact]
    public async Task Add_members_stops_at_the_member_limit()
    {
        var owner = await api.CreatePersonAsync();
        var a = await api.CreatePersonAsync();
        var extra = await api.CreatePersonAsync();
        var id = await CreateGroupAsync(owner, a);
        // Fill the group with filler rows up to the limit.
        await api.WithDbAsync(async db =>
        {
            var now = api.Time.GetUtcNow();
            var fillers = Enumerable.Range(0, 254).Select(_ => new Chat.Data.Entities.User
            {
                Id = Guid.NewGuid(), Username = Unique("f"), Email = Unique("f") + "@example.com", DisplayName = "f",
                Status = UserStatus.Active, CreatedAt = now, UpdatedAt = now,
            }).ToList();
            db.Users.AddRange(fillers);
            db.ConversationMembers.AddRange(fillers.Select(f => new Chat.Data.Entities.ConversationMember
                { ConversationId = id, UserId = f.Id, Role = MemberRole.Member, JoinedAt = now }));
            await db.SaveChangesAsync();
        });

        await (await AddAsync(owner, id, extra)).AssertErrorAsync(HttpStatusCode.Conflict, "group_full");
    }

    [Fact]
    public async Task Rejoining_resets_the_role_and_starts_with_nothing_unread()
    {
        var owner = await api.CreatePersonAsync();
        var a = await api.CreatePersonAsync();
        var id = await CreateGroupAsync(owner, a);
        (await SetRoleAsync(owner, id, a, "admin")).EnsureSuccessStatusCode();
        (await C.PostAsync(a, $"/conversations/{id}/leave")).EnsureSuccessStatusCode();
        await api.WithDbAsync(db => db.Conversations.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastSeq, 9L)));

        (await AddAsync(owner, id, a)).EnsureSuccessStatusCode();

        var mine = (await (await C.GetAsync(a, $"/conversations/{id}")).JsonAsync()).GetProperty("conversation");
        Assert.Equal("member", mine.GetProperty("role").GetString());
        Assert.Equal(0, mine.GetProperty("unread").GetInt64());
        Assert.Equal(9, mine.GetProperty("lastSeq").GetInt64());
    }

    [Fact]
    public async Task Owner_removes_anyone_admin_removes_only_members()
    {
        var owner = await api.CreatePersonAsync();
        var admin = await api.CreatePersonAsync();
        var otherAdmin = await api.CreatePersonAsync();
        var member = await api.CreatePersonAsync();
        var member2 = await api.CreatePersonAsync();
        var id = await CreateGroupAsync(owner, admin, otherAdmin, member, member2);
        (await SetRoleAsync(owner, id, admin, "admin")).EnsureSuccessStatusCode();
        (await SetRoleAsync(owner, id, otherAdmin, "admin")).EnsureSuccessStatusCode();

        await (await C.DeleteAsync(admin, $"/conversations/{id}/members/{otherAdmin.Id}")).AssertErrorAsync(HttpStatusCode.Forbidden, "insufficient_role");
        await (await C.DeleteAsync(admin, $"/conversations/{id}/members/{owner.Id}")).AssertErrorAsync(HttpStatusCode.Forbidden, "insufficient_role");
        await (await C.DeleteAsync(member, $"/conversations/{id}/members/{member2.Id}")).AssertErrorAsync(HttpStatusCode.Forbidden, "insufficient_role");
        Assert.Equal(HttpStatusCode.OK, (await C.DeleteAsync(admin, $"/conversations/{id}/members/{member.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await C.DeleteAsync(owner, $"/conversations/{id}/members/{otherAdmin.Id}")).StatusCode);

        Assert.Equal(new[] { owner.Id, admin.Id, member2.Id }.Order(), (await RolesAsync(owner, id)).Keys.Order());
        await (await C.GetAsync(member, $"/conversations/{id}")).AssertErrorAsync(HttpStatusCode.NotFound, "conversation_not_found");
    }

    [Fact]
    public async Task Remove_member_rejects_yourself_and_people_not_in_the_group()
    {
        var owner = await api.CreatePersonAsync();
        var a = await api.CreatePersonAsync();
        var outsider = await api.CreatePersonAsync();
        var id = await CreateGroupAsync(owner, a);

        await (await C.DeleteAsync(owner, $"/conversations/{id}/members/{owner.Id}")).AssertErrorAsync(HttpStatusCode.BadRequest, "invalid_target");
        await (await C.DeleteAsync(owner, $"/conversations/{id}/members/{outsider.Id}")).AssertErrorAsync(HttpStatusCode.NotFound, "member_not_found");
    }

    [Fact]
    public async Task Leaving_removes_the_conversation_from_my_list_but_not_the_others()
    {
        var owner = await api.CreatePersonAsync();
        var a = await api.CreatePersonAsync();
        var id = await CreateGroupAsync(owner, a);

        Assert.Equal(HttpStatusCode.NoContent, (await C.PostAsync(a, $"/conversations/{id}/leave")).StatusCode);

        await (await C.GetAsync(a, $"/conversations/{id}")).AssertErrorAsync(HttpStatusCode.NotFound, "conversation_not_found");
        Assert.Empty((await (await C.GetAsync(a, "/conversations")).JsonAsync()).EnumerateArray());
        Assert.Single(await RolesAsync(owner, id));
        await (await C.PostAsync(a, $"/conversations/{id}/leave")).AssertErrorAsync(HttpStatusCode.NotFound, "conversation_not_found");
    }

    [Fact]
    public async Task When_the_owner_leaves_the_longest_standing_admin_takes_over()
    {
        var owner = await api.CreatePersonAsync();
        var member = await api.CreatePersonAsync();
        var admin = await api.CreatePersonAsync();
        var id = await CreateGroupAsync(owner, member);
        api.Time.Advance(TimeSpan.FromMinutes(1));
        (await AddAsync(owner, id, admin)).EnsureSuccessStatusCode();
        (await SetRoleAsync(owner, id, admin, "admin")).EnsureSuccessStatusCode();

        (await C.PostAsync(owner, $"/conversations/{id}/leave")).EnsureSuccessStatusCode();

        var roles = await RolesAsync(member, id);
        Assert.Equal("owner", roles[admin.Id]);
        Assert.Equal("member", roles[member.Id]);
    }

    [Fact]
    public async Task When_the_owner_leaves_a_group_of_plain_members_the_earliest_one_takes_over()
    {
        var owner = await api.CreatePersonAsync();
        var first = await api.CreatePersonAsync();
        var second = await api.CreatePersonAsync();
        var id = await CreateGroupAsync(owner, first);
        api.Time.Advance(TimeSpan.FromMinutes(1));
        (await AddAsync(owner, id, second)).EnsureSuccessStatusCode();

        (await C.PostAsync(owner, $"/conversations/{id}/leave")).EnsureSuccessStatusCode();

        var roles = await RolesAsync(first, id);
        Assert.Equal("owner", roles[first.Id]);
        Assert.Equal("member", roles[second.Id]);
    }

    [Fact]
    public async Task The_last_member_can_leave()
    {
        var owner = await api.CreatePersonAsync();
        var a = await api.CreatePersonAsync();
        var id = await CreateGroupAsync(owner, a);
        (await C.PostAsync(a, $"/conversations/{id}/leave")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await C.PostAsync(owner, $"/conversations/{id}/leave")).StatusCode);
    }

    [Fact]
    public async Task Only_the_owner_sets_roles_and_not_their_own()
    {
        var owner = await api.CreatePersonAsync();
        var admin = await api.CreatePersonAsync();
        var member = await api.CreatePersonAsync();
        var outsider = await api.CreatePersonAsync();
        var id = await CreateGroupAsync(owner, admin, member);
        (await SetRoleAsync(owner, id, admin, "admin")).EnsureSuccessStatusCode();

        await (await SetRoleAsync(admin, id, member, "admin")).AssertErrorAsync(HttpStatusCode.Forbidden, "insufficient_role");
        await (await SetRoleAsync(owner, id, owner, "member")).AssertErrorAsync(HttpStatusCode.BadRequest, "invalid_target");
        await (await SetRoleAsync(owner, id, outsider, "admin")).AssertErrorAsync(HttpStatusCode.NotFound, "member_not_found");
        await (await SetRoleAsync(owner, id, member, "owner")).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("admin", (await RolesAsync(owner, id))[admin.Id]);

        (await SetRoleAsync(owner, id, admin, "member")).EnsureSuccessStatusCode();
        Assert.Equal("member", (await RolesAsync(owner, id))[admin.Id]);
    }

    [Fact]
    public async Task Parallel_leaves_by_the_owner_and_an_admin_leave_exactly_one_owner()
    {
        var owner = await api.CreatePersonAsync();
        var admin = await api.CreatePersonAsync();
        var member = await api.CreatePersonAsync();
        var id = await CreateGroupAsync(owner, admin, member);
        (await SetRoleAsync(owner, id, admin, "admin")).EnsureSuccessStatusCode();

        await Task.WhenAll(C.PostAsync(owner, $"/conversations/{id}/leave"), C.PostAsync(admin, $"/conversations/{id}/leave"));

        var roles = await api.WithDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == id && m.LeftAt == null).Select(m => m.Role).ToListAsync());
        Assert.Equal(MemberRole.Owner, Assert.Single(roles));
    }
}

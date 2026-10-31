using System.Text.Json;
using Chat.Data;
using Chat.Data.Entities;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using static Chat.Api.Tests.HubTestHelpers;
using static Chat.Api.Tests.SendMessageTests;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class EditDeleteTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static Task<JsonElement> EditAsync(HubConnection hub, Guid messageId, string body) =>
        hub.InvokeAsync<JsonElement>("EditMessage", messageId, body);

    private static Task<JsonElement> DeleteAsync(HubConnection hub, Guid messageId) =>
        hub.InvokeAsync<JsonElement>("DeleteMessage", messageId);

    /// <summary>Alice and Bob in a direct chat, both connected, with Alice's first message sent.</summary>
    private async Task<(Person Alice, Person Bob, Guid Chat, HubConnection AliceHub, HubConnection BobHub, Guid MessageId)> ArrangeAsync()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        var chat = await api.OpenDirectAsync(alice, bob);
        var aliceHub = await api.ConnectAsync(alice);
        var bobHub = await api.ConnectAsync(bob);
        var sent = await SendAsync(aliceHub, chat, "original");
        return (alice, bob, chat, aliceHub, bobHub, sent.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Sender_edits_and_everyone_gets_message_edited()
    {
        var (_, _, _, aliceHub, bobHub, id) = await ArrangeAsync();
        var bobInbox = Inbox.Listen(bobHub);

        var edited = await EditAsync(aliceHub, id, "  changed ");

        Assert.Equal("changed", edited.GetProperty("body").GetString());
        Assert.NotEqual(JsonValueKind.Null, edited.GetProperty("editedAt").ValueKind);
        var seen = await bobInbox.Next("message.edited");
        Assert.Equal(id, seen.GetProperty("id").GetGuid());
        Assert.Equal("changed", seen.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Another_member_cannot_edit_or_delete()
    {
        var (_, _, _, _, bobHub, id) = await ArrangeAsync();

        await AssertHubErrorAsync(() => EditAsync(bobHub, id, "mine now"), "message_not_found");
        await AssertHubErrorAsync(() => DeleteAsync(bobHub, id), "message_not_found");
        await AssertHubErrorAsync(() => EditAsync(bobHub, Guid.NewGuid(), "x"), "message_not_found");
    }

    [Fact]
    public async Task Editing_a_deleted_message_fails_and_bad_bodies_are_rejected()
    {
        var (_, _, _, aliceHub, _, id) = await ArrangeAsync();
        await AssertHubErrorAsync(() => EditAsync(aliceHub, id, " "), "validation_failed");
        await AssertHubErrorAsync(() => EditAsync(aliceHub, id, new string('x', 4001)), "validation_failed");

        await DeleteAsync(aliceHub, id);

        await AssertHubErrorAsync(() => EditAsync(aliceHub, id, "too late"), "message_deleted");
    }

    [Fact]
    public async Task Delete_sets_deleted_at_and_clears_the_body_in_the_database()
    {
        var (_, _, _, aliceHub, bobHub, id) = await ArrangeAsync();
        var bobInbox = Inbox.Listen(bobHub);

        var tombstone = await DeleteAsync(aliceHub, id);

        Assert.Equal(JsonValueKind.Null, tombstone.GetProperty("body").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, tombstone.GetProperty("deletedAt").ValueKind);
        var seen = await bobInbox.Next("message.deleted");
        Assert.Equal(id, seen.GetProperty("id").GetGuid());
        Assert.Equal(1, seen.GetProperty("seq").GetInt64());
        var row = await api.WithDbAsync(db => db.Messages.AsNoTracking().SingleAsync(m => m.Id == id));
        Assert.Null(row.Body);
        Assert.NotNull(row.DeletedAt);
    }

    [Fact]
    public async Task Deleting_twice_returns_the_tombstone_and_broadcasts_once()
    {
        var (_, _, _, aliceHub, bobHub, id) = await ArrangeAsync();
        var bobInbox = Inbox.Listen(bobHub);

        await DeleteAsync(aliceHub, id);
        var again = await DeleteAsync(aliceHub, id);

        Assert.NotEqual(JsonValueKind.Null, again.GetProperty("deletedAt").ValueKind);
        await bobInbox.Next("message.deleted");
        await bobInbox.ExpectNone("message.deleted");
    }

    [Fact]
    public async Task System_messages_cannot_be_edited_or_deleted()
    {
        var owner = await api.CreatePersonAsync();
        var member = await api.CreatePersonAsync();
        var chat = await api.CreateGroupAsync(owner, member);
        var id = Guid.NewGuid();
        await api.WithDbAsync(async db =>
        {
            db.Messages.Add(new Message { Id = id, ConversationId = chat, Seq = 1, ClientMsgId = Guid.NewGuid(), Type = MessageType.System, Body = "{}", CreatedAt = api.Time.GetUtcNow() });
            await db.Conversations.Where(c => c.Id == chat).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastSeq, 1L));
            await db.SaveChangesAsync();
        });
        await using var hub = await api.ConnectAsync(owner);

        await AssertHubErrorAsync(() => EditAsync(hub, id, "x"), "message_not_found");
        await AssertHubErrorAsync(() => DeleteAsync(hub, id), "message_not_found");
    }

    [Fact]
    public async Task A_member_who_left_cannot_edit_their_old_message()
    {
        var owner = await api.CreatePersonAsync();
        var member = await api.CreatePersonAsync();
        var chat = await api.CreateGroupAsync(owner, member);
        await using var hub = await api.ConnectAsync(member);
        var id = (await SendAsync(hub, chat, "bye")).GetProperty("id").GetGuid();
        (await api.Client.PostAsync(member, $"/conversations/{chat}/leave")).EnsureSuccessStatusCode();

        await AssertHubErrorAsync(() => EditAsync(hub, id, "x"), "message_not_found");
    }

    [Fact]
    public async Task Edits_in_a_blocked_direct_chat_are_refused()
    {
        var (alice, bob, _, aliceHub, bobHub, id) = await ArrangeAsync();

        (await api.Client.PutAsync(bob, $"/blocks/{alice.Id}")).EnsureSuccessStatusCode();
        await AssertHubErrorAsync(() => EditAsync(aliceHub, id, "x"), "user_not_found");
        await AssertHubErrorAsync(() => DeleteAsync(aliceHub, id), "user_not_found");
        (await api.Client.DeleteAsync(bob, $"/blocks/{alice.Id}")).EnsureSuccessStatusCode();
        (await api.Client.PutAsync(alice, $"/blocks/{bob.Id}")).EnsureSuccessStatusCode();
        await AssertHubErrorAsync(() => EditAsync(aliceHub, id, "x"), "blocked");
    }

    [Fact]
    public async Task Edits_do_not_use_the_send_budget()
    {
        var (_, _, chat, aliceHub, _, id) = await ArrangeAsync();
        for (var i = 0; i < 19; i++) await SendAsync(aliceHub, chat, $"m{i}");

        for (var i = 0; i < 5; i++) await EditAsync(aliceHub, id, $"v{i}");
    }
}

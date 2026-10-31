using System.Text.Json;
using Chat.Data;
using Chat.Data.Entities;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using static Chat.Api.Tests.HubTestHelpers;
using static Chat.Api.Tests.SendMessageTests;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class ReactionTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string Thumb = "👍";

    private static Task ReactAsync(HubConnection hub, Guid messageId, string emoji, bool on = true) =>
        hub.InvokeAsync("React", messageId, emoji, on);

    private Task<int> RowsAsync(Guid messageId) => api.WithDbAsync(db => db.MessageReactions.CountAsync(r => r.MessageId == messageId));

    private async Task<(Person Alice, Person Bob, Guid Chat, HubConnection AliceHub, HubConnection BobHub, Guid MessageId)> ArrangeAsync()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        var chat = await api.OpenDirectAsync(alice, bob);
        var aliceHub = await api.ConnectAsync(alice);
        var bobHub = await api.ConnectAsync(bob);
        var sent = await SendAsync(aliceHub, chat, "react to me");
        return (alice, bob, chat, aliceHub, bobHub, sent.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Reacting_broadcasts_to_all_members_and_stores_one_row()
    {
        var (alice, bob, chat, aliceHub, bobHub, id) = await ArrangeAsync();
        var aliceInbox = Inbox.Listen(aliceHub);

        await ReactAsync(bobHub, id, Thumb);

        var seen = await aliceInbox.Next("message.reaction");
        Assert.Equal(chat, seen.GetProperty("conversationId").GetGuid());
        Assert.Equal(id, seen.GetProperty("messageId").GetGuid());
        Assert.Equal(bob.Id, seen.GetProperty("userId").GetGuid());
        Assert.Equal(Thumb, seen.GetProperty("emoji").GetString());
        Assert.True(seen.GetProperty("on").GetBoolean());
        Assert.Equal(1, await RowsAsync(id));
    }

    [Fact]
    public async Task Reacting_on_twice_is_idempotent()
    {
        var (_, _, _, aliceHub, bobHub, id) = await ArrangeAsync();
        var aliceInbox = Inbox.Listen(aliceHub);

        await ReactAsync(bobHub, id, Thumb);
        await ReactAsync(bobHub, id, Thumb);

        await aliceInbox.Next("message.reaction");
        await aliceInbox.ExpectNone("message.reaction");
        Assert.Equal(1, await RowsAsync(id));
    }

    [Fact]
    public async Task Reacting_off_removes_it_and_off_when_absent_sends_nothing()
    {
        var (_, _, _, aliceHub, bobHub, id) = await ArrangeAsync();
        var aliceInbox = Inbox.Listen(aliceHub);
        await ReactAsync(bobHub, id, Thumb);
        Assert.True((await aliceInbox.Next("message.reaction")).GetProperty("on").GetBoolean()); // delivery is async: take it before the next step

        await ReactAsync(bobHub, id, Thumb, on: false);
        Assert.False((await aliceInbox.Next("message.reaction")).GetProperty("on").GetBoolean());
        await ReactAsync(bobHub, id, Thumb, on: false);

        await aliceInbox.ExpectNone("message.reaction");
        Assert.Equal(0, await RowsAsync(id));
    }

    [Fact]
    public async Task Reacting_to_a_deleted_message_fails()
    {
        var (_, _, _, aliceHub, bobHub, id) = await ArrangeAsync();
        await aliceHub.InvokeAsync<JsonElement>("DeleteMessage", id);

        await AssertHubErrorAsync(() => ReactAsync(bobHub, id, Thumb), "message_deleted");
    }

    [Fact]
    public async Task Reacting_to_a_system_message_fails()
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

        await AssertHubErrorAsync(() => ReactAsync(hub, id, Thumb), "message_not_found");
    }

    [Fact]
    public async Task Emoji_must_be_one_to_thirty_two_characters()
    {
        var (_, _, _, _, bobHub, id) = await ArrangeAsync();

        await AssertHubErrorAsync(() => ReactAsync(bobHub, id, ""), "validation_failed");
        await AssertHubErrorAsync(() => ReactAsync(bobHub, id, "  "), "validation_failed");
        await AssertHubErrorAsync(() => ReactAsync(bobHub, id, new string('e', 33)), "validation_failed");
        await ReactAsync(bobHub, id, new string('e', 32));
    }

    [Fact]
    public async Task A_non_member_cannot_react()
    {
        var (_, _, _, _, _, id) = await ArrangeAsync();
        var stranger = await api.CreatePersonAsync();
        await using var hub = await api.ConnectAsync(stranger);

        await AssertHubErrorAsync(() => ReactAsync(hub, id, Thumb), "message_not_found");
    }

    [Fact]
    public async Task Reactions_in_a_blocked_direct_chat_are_refused()
    {
        var (alice, bob, _, _, bobHub, id) = await ArrangeAsync();
        (await api.Client.PutAsync(alice, $"/blocks/{bob.Id}")).EnsureSuccessStatusCode();

        await AssertHubErrorAsync(() => ReactAsync(bobHub, id, Thumb), "user_not_found");
    }

    [Fact]
    public async Task Edited_events_carry_the_reaction_counts()
    {
        var (_, _, _, aliceHub, bobHub, id) = await ArrangeAsync();
        await ReactAsync(bobHub, id, Thumb);
        var bobInbox = Inbox.Listen(bobHub);

        await aliceHub.InvokeAsync<JsonElement>("EditMessage", id, "edited");

        var reactions = (await bobInbox.Next("message.edited")).GetProperty("reactions");
        Assert.Equal(1, reactions.GetArrayLength());
        Assert.Equal(Thumb, reactions[0].GetProperty("emoji").GetString());
        Assert.Equal(1, reactions[0].GetProperty("count").GetInt32());
    }
}

using System.Text.Json;
using Chat.Data;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Chat.Api.Tests.HubTestHelpers;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class SendMessageTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    internal static Task<JsonElement> SendAsync(HubConnection hub, Guid conversationId, string body, Guid? clientMsgId = null, Guid? replyToId = null) =>
        hub.InvokeAsync<JsonElement>("SendMessage", conversationId, clientMsgId ?? Guid.NewGuid(), body, replyToId);

    private async Task PutBlockAsync(Person blocker, Person blocked) =>
        (await api.Client.PutAsync(blocker, $"/blocks/{blocked.Id}")).EnsureSuccessStatusCode();

    private async Task<(Person Alice, Person Bob, Guid Chat, HubConnection AliceHub, HubConnection BobHub)> DirectChatAsync()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        return (alice, bob, await api.OpenDirectAsync(alice, bob), await api.ConnectAsync(alice), await api.ConnectAsync(bob));
    }

    [Fact]
    public async Task Send_returns_the_stored_message_and_both_members_receive_it()
    {
        var (alice, _, chat, aliceHub, bobHub) = await DirectChatAsync();
        var aliceInbox = Inbox.Listen(aliceHub);
        var bobInbox = Inbox.Listen(bobHub);

        var sent = await SendAsync(aliceHub, chat, "hello ");

        Assert.Equal("hello", sent.GetProperty("body").GetString());
        Assert.Equal(1, sent.GetProperty("seq").GetInt64());
        Assert.Equal("text", sent.GetProperty("type").GetString());
        Assert.Equal(alice.Id, sent.GetProperty("senderId").GetGuid());
        var id = sent.GetProperty("id").GetGuid();
        Assert.Equal(id, (await bobInbox.Next("message.created")).GetProperty("id").GetGuid());
        Assert.Equal(id, (await aliceInbox.Next("message.created")).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Senders_other_device_also_receives_it()
    {
        var (alice, _, chat, aliceHub, _) = await DirectChatAsync();
        await using var phone = await api.ConnectAsync(alice);
        var phoneInbox = Inbox.Listen(phone);

        var sent = await SendAsync(aliceHub, chat, "hi");

        Assert.Equal(sent.GetProperty("id").GetGuid(), (await phoneInbox.Next("message.created")).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Messages_get_consecutive_seq()
    {
        var (_, _, chat, aliceHub, _) = await DirectChatAsync();

        var first = await SendAsync(aliceHub, chat, "one");
        var second = await SendAsync(aliceHub, chat, "two");

        Assert.Equal(1, first.GetProperty("seq").GetInt64());
        Assert.Equal(2, second.GetProperty("seq").GetInt64());
    }

    [Fact]
    public async Task Concurrent_sends_get_distinct_gapless_seq()
    {
        var owner = await api.CreatePersonAsync();
        var a = await api.CreatePersonAsync();
        var b = await api.CreatePersonAsync();
        var chat = await api.CreateGroupAsync(owner, a, b);
        var hubs = new[] { await api.ConnectAsync(owner), await api.ConnectAsync(a), await api.ConnectAsync(b) };

        var results = await Task.WhenAll(hubs.SelectMany(h => Enumerable.Range(0, 10).Select(i => SendAsync(h, chat, $"m{i}"))));

        Assert.Equal(Enumerable.Range(1, 30).Select(i => (long)i), results.Select(r => r.GetProperty("seq").GetInt64()).Order());
    }

    [Fact]
    public async Task Retry_with_same_clientMsgId_returns_the_original_and_broadcasts_once()
    {
        var (_, _, chat, aliceHub, bobHub) = await DirectChatAsync();
        var bobInbox = Inbox.Listen(bobHub);
        var clientMsgId = Guid.NewGuid();

        var first = await SendAsync(aliceHub, chat, "once", clientMsgId);
        var retry = await SendAsync(aliceHub, chat, "once", clientMsgId);

        Assert.Equal(first.GetProperty("id").GetGuid(), retry.GetProperty("id").GetGuid());
        Assert.Equal(first.GetProperty("seq").GetInt64(), retry.GetProperty("seq").GetInt64());
        await bobInbox.Next("message.created");
        await bobInbox.ExpectNone("message.created");
    }

    [Fact]
    public async Task Concurrent_retry_of_the_same_clientMsgId_creates_one_row()
    {
        var (_, _, chat, aliceHub, _) = await DirectChatAsync();
        var clientMsgId = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => SendAsync(aliceHub, chat, "race", clientMsgId)));

        Assert.Equal(results[0].GetProperty("id").GetGuid(), results[1].GetProperty("id").GetGuid());
        Assert.Equal(1, await api.WithDbAsync(db => db.Messages.CountAsync(m => m.ClientMsgId == clientMsgId)));
    }

    [Fact]
    public async Task Sender_cursors_move_to_the_new_seq_and_other_members_do_not()
    {
        var (alice, bob, chat, aliceHub, _) = await DirectChatAsync();

        await SendAsync(aliceHub, chat, "a");
        var sent = await SendAsync(aliceHub, chat, "b");

        var seq = sent.GetProperty("seq").GetInt64();
        var members = await api.WithDbAsync(db => db.ConversationMembers.Where(m => m.ConversationId == chat).ToListAsync());
        var mine = members.Single(m => m.UserId == alice.Id);
        Assert.Equal((seq, seq), (mine.LastReadSeq, mine.LastDeliveredSeq));
        var theirs = members.Single(m => m.UserId == bob.Id);
        Assert.Equal((0L, 0L), (theirs.LastReadSeq, theirs.LastDeliveredSeq));
    }

    [Fact]
    public async Task Reply_works_in_the_same_conversation_and_not_across_conversations()
    {
        var (alice, bob, chat, aliceHub, _) = await DirectChatAsync();
        var carol = await api.CreatePersonAsync();
        var otherChat = await api.OpenDirectAsync(alice, carol);
        var original = await SendAsync(aliceHub, chat, "original");
        var elsewhere = await SendAsync(aliceHub, otherChat, "elsewhere");

        var reply = await SendAsync(aliceHub, chat, "reply", replyToId: original.GetProperty("id").GetGuid());

        Assert.Equal(original.GetProperty("id").GetGuid(), reply.GetProperty("replyToId").GetGuid());
        await AssertHubErrorAsync(() => SendAsync(aliceHub, chat, "bad", replyToId: elsewhere.GetProperty("id").GetGuid()), "validation_failed");
        await AssertHubErrorAsync(() => SendAsync(aliceHub, chat, "bad", replyToId: Guid.NewGuid()), "validation_failed");
    }

    [Fact]
    public async Task Non_members_and_members_who_left_get_conversation_not_found()
    {
        var owner = await api.CreatePersonAsync();
        var member = await api.CreatePersonAsync();
        var stranger = await api.CreatePersonAsync();
        var chat = await api.CreateGroupAsync(owner, member);
        await using var strangerHub = await api.ConnectAsync(stranger);
        await using var memberHub = await api.ConnectAsync(member);
        (await api.Client.PostAsync(member, $"/conversations/{chat}/leave")).EnsureSuccessStatusCode();

        await AssertHubErrorAsync(() => SendAsync(strangerHub, chat, "hi"), "conversation_not_found");
        await AssertHubErrorAsync(() => SendAsync(memberHub, chat, "hi"), "conversation_not_found");
        await AssertHubErrorAsync(() => SendAsync(strangerHub, Guid.NewGuid(), "hi"), "conversation_not_found");
    }

    /// <summary>
    /// The validator runs before the conversation lock, so a member removed in between reaches the writer. The writer checks again
    /// under the lock, which is what this calls directly.
    /// </summary>
    [Fact]
    public async Task A_member_removed_after_the_check_still_cannot_store_a_message()
    {
        var owner = await api.CreatePersonAsync();
        var member = await api.CreatePersonAsync();
        var chat = await api.CreateGroupAsync(owner, member);
        (await api.Client.DeleteAsync(owner, $"/conversations/{chat}/members/{member.Id}")).EnsureSuccessStatusCode();

        await using var scope = api.Services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<Chat.Api.Messaging.MessageWriter>();
        var result = await writer.SendAsync(member.Id, chat, Guid.NewGuid(), "slipped in", null, null, CancellationToken.None);

        Assert.Equal("conversation_not_found", result.Error?.Code);
        Assert.False(await api.WithDbAsync(db => db.Messages.AnyAsync(m => m.ConversationId == chat && m.SenderId == member.Id)));
    }

    [Fact]
    public async Task Body_limits_are_enforced()
    {
        var (_, _, chat, aliceHub, _) = await DirectChatAsync();

        await AssertHubErrorAsync(() => SendAsync(aliceHub, chat, ""), "validation_failed");
        await AssertHubErrorAsync(() => SendAsync(aliceHub, chat, " \n\t "), "validation_failed");
        await AssertHubErrorAsync(() => SendAsync(aliceHub, chat, new string('x', 4001)), "validation_failed");
        var longest = await SendAsync(aliceHub, chat, new string('x', 4000));
        Assert.Equal(4000, longest.GetProperty("body").GetString()!.Length);
    }

    [Fact]
    public async Task Empty_clientMsgId_is_rejected()
    {
        var (_, _, chat, aliceHub, _) = await DirectChatAsync();

        await AssertHubErrorAsync(() => SendAsync(aliceHub, chat, "hi", Guid.Empty), "validation_failed");
    }

    [Fact]
    public async Task Reusing_a_clientMsgId_in_another_conversation_conflicts()
    {
        var (alice, _, chat, aliceHub, _) = await DirectChatAsync();
        var carol = await api.CreatePersonAsync();
        var otherChat = await api.OpenDirectAsync(alice, carol);
        var clientMsgId = Guid.NewGuid();
        await SendAsync(aliceHub, chat, "first", clientMsgId);

        await AssertHubErrorAsync(() => SendAsync(aliceHub, otherChat, "second", clientMsgId), "conflict");

        Assert.Equal(0, await api.WithDbAsync(db => db.Messages.CountAsync(m => m.ConversationId == otherChat)));
    }

    [Fact]
    public async Task Different_users_may_use_the_same_clientMsgId()
    {
        var (_, _, chat, aliceHub, bobHub) = await DirectChatAsync();
        var clientMsgId = Guid.NewGuid();

        var a = await SendAsync(aliceHub, chat, "a", clientMsgId);
        var b = await SendAsync(bobHub, chat, "b", clientMsgId);

        Assert.NotEqual(a.GetProperty("id").GetGuid(), b.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Blocks_in_a_direct_chat_are_refused_in_both_directions()
    {
        var (alice, bob, chat, aliceHub, bobHub) = await DirectChatAsync();

        await PutBlockAsync(bob, alice);
        await AssertHubErrorAsync(() => SendAsync(aliceHub, chat, "hi"), "user_not_found");
        (await api.Client.DeleteAsync(bob, $"/blocks/{alice.Id}")).EnsureSuccessStatusCode();
        await PutBlockAsync(alice, bob);
        await AssertHubErrorAsync(() => SendAsync(aliceHub, chat, "hi"), "blocked");
        await AssertHubErrorAsync(() => SendAsync(bobHub, chat, "hi"), "user_not_found");
    }

    [Fact]
    public async Task A_block_between_two_group_members_does_not_stop_the_group()
    {
        var owner = await api.CreatePersonAsync();
        var a = await api.CreatePersonAsync();
        var b = await api.CreatePersonAsync();
        var chat = await api.CreateGroupAsync(owner, a, b);
        await PutBlockAsync(a, b);
        await using var hub = await api.ConnectAsync(b);

        var sent = await SendAsync(hub, chat, "still here");

        Assert.Equal(1, sent.GetProperty("seq").GetInt64());
    }
}

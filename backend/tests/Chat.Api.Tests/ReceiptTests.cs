using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using static Chat.Api.Tests.HubTestHelpers;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class ReceiptTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private async Task<(Person Alice, Person Bob, Guid Chat, HubConnection AliceHub, HubConnection BobHub)> ChatWithMessagesAsync(int messages)
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        var chat = await api.OpenDirectAsync(alice, bob);
        var aliceHub = await api.ConnectAsync(alice);
        var bobHub = await api.ConnectAsync(bob);
        for (var i = 0; i < messages; i++) await SendMessageTests.SendAsync(aliceHub, chat, $"m{i}");
        return (alice, bob, chat, aliceHub, bobHub);
    }

    private async Task<JsonElement> MemberAsync(Person viewer, Guid chat, Guid userId)
    {
        var detail = await (await api.Client.GetAsync(viewer, $"/conversations/{chat}")).JsonAsync();
        return detail.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("userId").GetGuid() == userId);
    }

    [Fact]
    public async Task MarkRead_moves_the_cursor_and_counts_as_delivered_and_both_members_hear_of_it()
    {
        var (alice, bob, chat, aliceHub, bobHub) = await ChatWithMessagesAsync(3);
        var aliceInbox = Inbox.Listen(aliceHub);

        await bobHub.InvokeAsync("MarkRead", chat, 2L);

        var receipt = await aliceInbox.Next("receipt.updated");
        Assert.Equal(bob.Id, receipt.GetProperty("userId").GetGuid());
        Assert.Equal(2, receipt.GetProperty("lastReadSeq").GetInt64());
        Assert.Equal(2, receipt.GetProperty("lastDeliveredSeq").GetInt64());
        var member = await MemberAsync(alice, chat, bob.Id);
        Assert.Equal(2, member.GetProperty("lastReadSeq").GetInt64());
        Assert.Equal(2, member.GetProperty("lastDeliveredSeq").GetInt64());
    }

    [Fact]
    public async Task MarkDelivered_does_not_touch_the_read_cursor()
    {
        var (alice, bob, chat, _, bobHub) = await ChatWithMessagesAsync(3);

        await bobHub.InvokeAsync("MarkDelivered", chat, 3L);

        var member = await MemberAsync(alice, chat, bob.Id);
        Assert.Equal(3, member.GetProperty("lastDeliveredSeq").GetInt64());
        Assert.Equal(0, member.GetProperty("lastReadSeq").GetInt64());
    }

    [Fact]
    public async Task Cursors_never_move_back_and_a_stale_call_sends_no_event()
    {
        var (alice, bob, chat, aliceHub, bobHub) = await ChatWithMessagesAsync(3);
        var aliceInbox = Inbox.Listen(aliceHub);
        await bobHub.InvokeAsync("MarkRead", chat, 3L);
        await aliceInbox.Next("receipt.updated"); // the real move; everything after it is stale

        await bobHub.InvokeAsync("MarkRead", chat, 1L);
        await bobHub.InvokeAsync("MarkDelivered", chat, 2L);
        await bobHub.InvokeAsync("MarkRead", chat, 3L);

        await aliceInbox.ExpectNone("receipt.updated");
        var member = await MemberAsync(alice, chat, bob.Id);
        Assert.Equal(3, member.GetProperty("lastReadSeq").GetInt64());
        Assert.Equal(3, member.GetProperty("lastDeliveredSeq").GetInt64());
    }

    [Fact]
    public async Task Reading_past_what_exists_is_rejected()
    {
        var (_, _, chat, _, bobHub) = await ChatWithMessagesAsync(2);

        await AssertHubErrorAsync(() => bobHub.InvokeAsync("MarkRead", chat, 3L), "validation_failed");
        await AssertHubErrorAsync(() => bobHub.InvokeAsync("MarkDelivered", chat, -1L), "validation_failed");
    }

    [Fact]
    public async Task The_callers_other_device_hears_of_it_so_unread_counts_follow()
    {
        var (_, bob, chat, _, bobHub) = await ChatWithMessagesAsync(2);
        await using var bobPhone = await api.ConnectAsync(bob);
        var phoneInbox = Inbox.Listen(bobPhone);

        await bobHub.InvokeAsync("MarkRead", chat, 2L);

        Assert.Equal(2, (await phoneInbox.Next("receipt.updated")).GetProperty("lastReadSeq").GetInt64());
        var summary = await (await api.Client.GetAsync(bob, "/conversations")).JsonAsync();
        Assert.Equal(0, summary.EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == chat).GetProperty("unread").GetInt64());
    }

    [Fact]
    public async Task A_non_member_is_refused()
    {
        var (_, _, chat, _, _) = await ChatWithMessagesAsync(1);
        var carol = await api.CreatePersonAsync();
        await using var carolHub = await api.ConnectAsync(carol);

        await AssertHubErrorAsync(() => carolHub.InvokeAsync("MarkRead", chat, 1L), "conversation_not_found");
    }

    [Fact]
    public async Task A_blocked_direct_chat_refuses_receipts_without_telling_the_blocked_person()
    {
        var (alice, bob, chat, aliceHub, bobHub) = await ChatWithMessagesAsync(1);
        (await api.Client.PutAsync(alice, $"/blocks/{bob.Id}")).EnsureSuccessStatusCode();

        await AssertHubErrorAsync(() => aliceHub.InvokeAsync("MarkRead", chat, 1L), "blocked");
        await AssertHubErrorAsync(() => bobHub.InvokeAsync("MarkRead", chat, 1L), "user_not_found");
    }

    [Fact]
    public async Task A_stale_session_is_refused()
    {
        var (_, bob, chat, _, bobHub) = await ChatWithMessagesAsync(1);
        (await api.Client.PostAsync(bob, "/auth/logout")).EnsureSuccessStatusCode();

        await AssertHubErrorAsync(() => bobHub.InvokeAsync("MarkRead", chat, 1L), "session_ended");
    }
}

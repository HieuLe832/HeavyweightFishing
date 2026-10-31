using Microsoft.AspNetCore.SignalR.Client;
using static Chat.Api.Tests.HubTestHelpers;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class TypingTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private async Task<(Person Alice, Person Bob, Guid Chat, HubConnection AliceHub, HubConnection BobHub)> DirectChatAsync()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        return (alice, bob, await api.OpenDirectAsync(alice, bob), await api.ConnectAsync(alice), await api.ConnectAsync(bob));
    }

    [Fact]
    public async Task Typing_reaches_the_other_members_but_not_the_typer()
    {
        var (alice, _, chat, aliceHub, bobHub) = await DirectChatAsync();
        var aliceInbox = Inbox.Listen(aliceHub);
        var bobInbox = Inbox.Listen(bobHub);

        await aliceHub.InvokeAsync("SetTyping", chat, true);

        var typing = await bobInbox.Next("typing");
        Assert.Equal(alice.Id, typing.GetProperty("userId").GetGuid());
        Assert.Equal(chat, typing.GetProperty("conversationId").GetGuid());
        Assert.True(typing.GetProperty("typing").GetBoolean());
        await aliceInbox.ExpectNone("typing");
    }

    [Fact]
    public async Task Stopping_clears_the_state_and_tells_the_others()
    {
        var (alice, bob, chat, aliceHub, bobHub) = await DirectChatAsync();
        var bobInbox = Inbox.Listen(bobHub);
        await aliceHub.InvokeAsync("SetTyping", chat, true);
        await bobInbox.Next("typing");
        Assert.Contains(alice.Id, await TypingNowAsync(bob, chat));

        await aliceHub.InvokeAsync("SetTyping", chat, false);

        Assert.False((await bobInbox.Next("typing")).GetProperty("typing").GetBoolean());
        Assert.Empty(await TypingNowAsync(bob, chat));
    }

    [Fact]
    public async Task Typing_lapses_when_it_is_not_repeated()
    {
        var (alice, bob, chat, aliceHub, _) = await DirectChatAsync();
        await aliceHub.InvokeAsync("SetTyping", chat, true);
        Assert.Contains(alice.Id, await TypingNowAsync(bob, chat));

        api.Time.Advance(TimeSpan.FromSeconds(6));

        Assert.Empty(await TypingNowAsync(bob, chat));
    }

    [Fact]
    public async Task Sending_a_message_ends_the_senders_typing()
    {
        var (alice, bob, chat, aliceHub, _) = await DirectChatAsync();
        await aliceHub.InvokeAsync("SetTyping", chat, true);

        await SendMessageTests.SendAsync(aliceHub, chat, "done");

        Assert.DoesNotContain(alice.Id, await TypingNowAsync(bob, chat));
    }

    [Fact]
    public async Task Signals_over_the_limit_are_dropped_not_refused()
    {
        var (_, _, chat, aliceHub, bobHub) = await DirectChatAsync();
        var bobInbox = Inbox.Listen(bobHub);
        api.Time.Advance(TimeSpan.FromMinutes(1)); // an empty window, whatever earlier tests did

        for (var i = 0; i < 12; i++) await aliceHub.InvokeAsync("SetTyping", chat, true);

        for (var i = 0; i < 10; i++) await bobInbox.Next("typing");
        await bobInbox.ExpectNone("typing");
    }

    [Fact]
    public async Task A_non_member_cannot_type_into_a_chat()
    {
        var (_, _, chat, _, _) = await DirectChatAsync();
        var carol = await api.CreatePersonAsync();
        await using var carolHub = await api.ConnectAsync(carol);

        await AssertHubErrorAsync(() => carolHub.InvokeAsync("SetTyping", chat, true), "conversation_not_found");
    }

    [Fact]
    public async Task A_stale_session_is_refused()
    {
        var (alice, _, chat, aliceHub, _) = await DirectChatAsync();
        (await api.Client.PostAsync(alice, "/auth/logout")).EnsureSuccessStatusCode();

        await AssertHubErrorAsync(() => aliceHub.InvokeAsync("SetTyping", chat, true), "session_ended");
    }

    private async Task<List<Guid>> TypingNowAsync(Person viewer, Guid chat) =>
        (await (await api.Client.GetAsync(viewer, $"/conversations/{chat}/presence")).JsonAsync())
            .GetProperty("typing").EnumerateArray().Select(e => e.GetGuid()).ToList();
}

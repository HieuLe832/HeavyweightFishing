using Microsoft.AspNetCore.SignalR.Client;
using static Chat.Api.Tests.HubTestHelpers;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class PresenceTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private async Task<List<Guid>> OnlineAsync(Person viewer, Guid chat) =>
        (await (await api.Client.GetAsync(viewer, $"/conversations/{chat}/presence")).JsonAsync())
            .GetProperty("online").EnumerateArray().Select(e => e.GetGuid()).ToList();

    [Fact]
    public async Task The_first_connection_announces_online_and_the_last_disconnect_announces_offline()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        await api.OpenDirectAsync(alice, bob);
        await using var bobHub = await api.ConnectAsync(bob);
        var bobInbox = Inbox.Listen(bobHub);

        var aliceLaptop = await api.ConnectAsync(alice);
        var online = await bobInbox.Next("presence.changed");
        Assert.Equal(alice.Id, online.GetProperty("userId").GetGuid());
        Assert.True(online.GetProperty("online").GetBoolean());

        var alicePhone = await api.ConnectAsync(alice);
        await bobInbox.ExpectNone("presence.changed"); // already online

        await aliceLaptop.StopAsync();
        await bobInbox.ExpectNone("presence.changed"); // the phone is still connected

        await alicePhone.StopAsync();
        var offline = await bobInbox.Next("presence.changed");
        Assert.Equal(alice.Id, offline.GetProperty("userId").GetGuid());
        Assert.False(offline.GetProperty("online").GetBoolean());
    }

    /// <summary>The user ids of the presence events that arrive within a short wait.</summary>
    private static async Task<List<Guid>> DrainPresenceAsync(Inbox inbox)
    {
        var ids = new List<Guid>();
        try
        {
            while (true) ids.Add((await inbox.Next("presence.changed", TimeSpan.FromMilliseconds(500))).GetProperty("userId").GetGuid());
        }
        catch (Xunit.Sdk.XunitException)
        {
            return ids; // nothing more arrived
        }
    }

    private async Task<(HubConnection Hub, Inbox Inbox)> ListenFromTheStartAsync(Person who)
    {
        var hub = api.NewHub(who.Token);
        var inbox = Inbox.Listen(hub);
        await hub.StartAsync();
        return (hub, inbox);
    }

    [Fact]
    public async Task Only_people_who_share_a_conversation_are_told_and_a_block_hides_it_either_way()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        var carol = await api.CreatePersonAsync();
        var stranger = await api.CreatePersonAsync();
        var group = await api.CreateGroupAsync(alice, bob, carol);
        (await api.Client.PutAsync(bob, $"/blocks/{alice.Id}")).EnsureSuccessStatusCode();
        var (bobHub, bobInbox) = await ListenFromTheStartAsync(bob);
        var (carolHub, carolInbox) = await ListenFromTheStartAsync(carol);
        var (strangerHub, strangerInbox) = await ListenFromTheStartAsync(stranger);

        await using var aliceHub = await api.ConnectAsync(alice);

        Assert.Contains(alice.Id, await DrainPresenceAsync(carolInbox));
        Assert.DoesNotContain(alice.Id, await DrainPresenceAsync(bobInbox)); // bob blocked alice
        Assert.Empty(await DrainPresenceAsync(strangerInbox)); // shares no conversation with anyone
        Assert.DoesNotContain(alice.Id, await OnlineAsync(bob, group));
        Assert.Contains(alice.Id, await OnlineAsync(carol, group));
        await bobHub.DisposeAsync();
        await carolHub.DisposeAsync();
        await strangerHub.DisposeAsync();
    }

    [Fact]
    public async Task The_presence_endpoint_lists_who_is_online_and_hides_the_caller_and_the_offline()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        var chat = await api.OpenDirectAsync(alice, bob);
        await using var aliceHub = await api.ConnectAsync(alice);
        Assert.Empty(await OnlineAsync(alice, chat));

        await using var bobHub = await api.ConnectAsync(bob);

        Assert.Equal([bob.Id], await OnlineAsync(alice, chat));
    }

    [Fact]
    public async Task The_presence_endpoint_refuses_non_members()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        var chat = await api.OpenDirectAsync(alice, bob);
        var carol = await api.CreatePersonAsync();

        await (await api.Client.GetAsync(carol, $"/conversations/{chat}/presence"))
            .AssertErrorAsync(System.Net.HttpStatusCode.NotFound, "conversation_not_found");
    }

    [Fact]
    public async Task A_presence_store_outage_does_not_stop_connecting_sending_or_receipts()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        var chat = await api.OpenDirectAsync(alice, bob);
        api.Presence.Fail = true;
        try
        {
            await using var aliceHub = await api.ConnectAsync(alice);
            await using var bobHub = await api.ConnectAsync(bob);
            var bobInbox = Inbox.Listen(bobHub);

            await SendMessageTests.SendAsync(aliceHub, chat, "still works");
            await bobInbox.Next("message.created");
            await bobHub.InvokeAsync("MarkRead", chat, 1L);
            await bobHub.InvokeAsync("SetTyping", chat, true);
            Assert.Empty(await OnlineAsync(alice, chat));
        }
        finally
        {
            api.Presence.Fail = false;
        }
    }
}

using static Chat.Api.Tests.HubTestHelpers;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

/// <summary>Two API nodes on one database and one Redis, as in a scaled-out deployment.</summary>
public class RedisApiFixture : ApiFixture
{
    protected override async Task<Dictionary<string, string?>> SettingsAsync() =>
        new() { ["Redis:ConnectionString"] = await SharedRedis.ConnectionStringAsync() };
}

public class TwoNodeTests(RedisApiFixture api) : IClassFixture<RedisApiFixture>
{
    [Fact]
    public async Task A_message_sent_through_one_node_reaches_a_client_on_the_other()
    {
        var node2 = api.AddNode();
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        var chat = await api.OpenDirectAsync(alice, bob);
        await using var aliceHub = await api.ConnectAsync(alice);
        var bobHub = node2.NewHub(bob.Token);
        await bobHub.StartAsync();
        var bobInbox = Inbox.Listen(bobHub);

        var sent = await SendMessageTests.SendAsync(aliceHub, chat, "across nodes");

        var received = await bobInbox.Next("message.created");
        Assert.Equal(sent.GetProperty("id").GetGuid(), received.GetProperty("id").GetGuid());
        await bobHub.DisposeAsync();
    }

    [Fact]
    public async Task Presence_set_on_one_node_is_seen_and_announced_on_the_other()
    {
        var node2 = api.AddNode();
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        var chat = await api.OpenDirectAsync(alice, bob);
        var bobHub = node2.NewHub(bob.Token);
        await bobHub.StartAsync();
        var bobInbox = Inbox.Listen(bobHub);

        await using var aliceHub = await api.ConnectAsync(alice); // on node 1

        var announced = await bobInbox.Next("presence.changed");
        Assert.Equal(alice.Id, announced.GetProperty("userId").GetGuid());
        var onNode2 = await (await node2.NewClient().GetAsync(bob, $"/conversations/{chat}/presence")).JsonAsync();
        Assert.Equal([alice.Id], onNode2.GetProperty("online").EnumerateArray().Select(e => e.GetGuid()));
        await bobHub.DisposeAsync();
    }
}

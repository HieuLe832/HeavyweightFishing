using System.Text.Json;
using static Chat.Api.Tests.HubTestHelpers;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class OfflineHandoffTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private JsonElement[] PublishedFor(Guid conversationId) => api.Bus.Published
        .Where(p => p.RoutingKey == "message.created" && p.Body.GetProperty("conversationId").GetGuid() == conversationId)
        .Select(p => p.Body).ToArray();

    [Fact]
    public async Task Members_with_no_live_connection_are_handed_to_the_bus_and_online_ones_and_the_sender_are_not()
    {
        var owner = await api.CreatePersonAsync();
        var online = await api.CreatePersonAsync();
        var offline = await api.CreatePersonAsync();
        var chat = await api.CreateGroupAsync(owner, online, offline);
        await using var ownerHub = await api.ConnectAsync(owner);
        await using var onlineHub = await api.ConnectAsync(online);

        var sent = await SendMessageTests.SendAsync(ownerHub, chat, "anyone there?");

        var events = PublishedFor(chat);
        var published = Assert.Single(events);
        Assert.Equal(sent.GetProperty("id").GetGuid(), published.GetProperty("messageId").GetGuid());
        Assert.Equal(1, published.GetProperty("seq").GetInt64());
        Assert.Equal(owner.Id, published.GetProperty("senderId").GetGuid());
        Assert.Equal([offline.Id], published.GetProperty("offlineUserIds").EnumerateArray().Select(e => e.GetGuid()));
        Assert.False(published.TryGetProperty("body", out _)); // ids only, never text
    }

    [Fact]
    public async Task Nothing_is_published_when_everyone_is_online()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        var chat = await api.OpenDirectAsync(alice, bob);
        await using var aliceHub = await api.ConnectAsync(alice);
        await using var bobHub = await api.ConnectAsync(bob);

        await SendMessageTests.SendAsync(aliceHub, chat, "hi");

        Assert.Empty(PublishedFor(chat));
    }

    [Fact]
    public async Task A_retried_send_is_not_published_twice()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        var chat = await api.OpenDirectAsync(alice, bob);
        await using var aliceHub = await api.ConnectAsync(alice);
        var clientMsgId = Guid.NewGuid();

        await SendMessageTests.SendAsync(aliceHub, chat, "once", clientMsgId);
        await SendMessageTests.SendAsync(aliceHub, chat, "once", clientMsgId);

        Assert.Single(PublishedFor(chat));
    }

    [Fact]
    public async Task System_notices_are_not_published()
    {
        var owner = await api.CreatePersonAsync();
        var member = await api.CreatePersonAsync();
        var newcomer = await api.CreatePersonAsync();
        var chat = await api.CreateGroupAsync(owner, member);

        (await api.Client.PostAsync(owner, $"/conversations/{chat}/members", new { userIds = new[] { newcomer.Id } })).EnsureSuccessStatusCode();

        Assert.Empty(PublishedFor(chat));
    }

    [Fact]
    public async Task A_presence_outage_counts_everyone_as_offline_so_nobody_misses_a_push()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        var chat = await api.OpenDirectAsync(alice, bob);
        await using var aliceHub = await api.ConnectAsync(alice);
        await using var bobHub = await api.ConnectAsync(bob);
        api.Presence.Fail = true;
        try
        {
            await SendMessageTests.SendAsync(aliceHub, chat, "hi");
        }
        finally
        {
            api.Presence.Fail = false;
        }

        var published = Assert.Single(PublishedFor(chat));
        Assert.Equal([bob.Id], published.GetProperty("offlineUserIds").EnumerateArray().Select(e => e.GetGuid()));
    }
}

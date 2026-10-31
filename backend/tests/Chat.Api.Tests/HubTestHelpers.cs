using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public static class HubTestHelpers
{
    public static readonly string[] EventNames = ["message.created", "message.edited", "message.deleted", "message.reaction"];

    public static async Task<HubConnection> ConnectAsync(this ApiFixture api, Person who)
    {
        var hub = api.NewHub(who.Token);
        await hub.StartAsync();
        return hub;
    }

    public static async Task<Guid> OpenDirectAsync(this ApiFixture api, Person a, Person b)
    {
        var response = await api.Client.PostAsync(a, "/conversations/direct", new { userId = b.Id });
        response.EnsureSuccessStatusCode();
        return (await response.JsonAsync()).GetProperty("conversation").GetProperty("id").GetGuid();
    }

    public static async Task<Guid> CreateGroupAsync(this ApiFixture api, Person owner, params Person[] members)
    {
        var response = await api.Client.PostAsync(owner, "/conversations/group", new { title = "g", memberIds = members.Select(m => m.Id) });
        response.EnsureSuccessStatusCode();
        return (await response.JsonAsync()).GetProperty("conversation").GetProperty("id").GetGuid();
    }

    /// <summary>The server throws HubException(code); SignalR delivers it as "...error on the server. HubException: code".</summary>
    public static async Task AssertHubErrorAsync(Func<Task> call, string code)
    {
        var e = await Assert.ThrowsAsync<HubException>(call);
        Assert.EndsWith($"HubException: {code}", e.Message);
    }

    /// <summary>Collects the events a connection receives so a test can wait for, or rule out, the next one.</summary>
    public sealed class Inbox
    {
        private readonly Dictionary<string, Channel<JsonElement>> _channels = EventNames.ToDictionary(n => n, _ => Channel.CreateUnbounded<JsonElement>());

        public static Inbox Listen(HubConnection connection)
        {
            var inbox = new Inbox();
            foreach (var name in EventNames)
                connection.On<JsonElement>(name, payload => inbox._channels[name].Writer.TryWrite(payload));
            return inbox;
        }

        public async Task<JsonElement> Next(string eventName, TimeSpan? timeout = null)
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
            try
            {
                return await _channels[eventName].Reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new Xunit.Sdk.XunitException($"No '{eventName}' event arrived.");
            }
        }

        public async Task ExpectNone(string eventName, TimeSpan? wait = null)
        {
            await Task.Delay(wait ?? TimeSpan.FromMilliseconds(300));
            Assert.False(_channels[eventName].Reader.TryRead(out _), $"Unexpected '{eventName}' event.");
        }
    }
}

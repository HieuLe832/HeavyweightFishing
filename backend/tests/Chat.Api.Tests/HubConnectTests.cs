using Microsoft.AspNetCore.SignalR.Client;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class HubConnectTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Valid_token_connects()
    {
        var who = await api.CreatePersonAsync();

        await using var hub = await api.ConnectAsync(who);

        Assert.Equal(HubConnectionState.Connected, hub.State);
    }

    [Fact]
    public async Task Missing_token_is_rejected()
    {
        await using var hub = api.NewHub("");

        await Assert.ThrowsAnyAsync<Exception>(() => hub.StartAsync());
    }

    [Fact]
    public async Task Revoked_session_is_rejected_on_connect()
    {
        var who = await api.CreatePersonAsync();
        (await api.Client.PostAsync(who, "/auth/logout")).EnsureSuccessStatusCode();
        await using var hub = api.NewHub(who.Token);

        await Assert.ThrowsAnyAsync<Exception>(() => hub.StartAsync());
    }
}

using Chat.Api.Messaging;
using Chat.Data;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>Completes when the server closes the connection (the test client does not reconnect).</summary>
    private static Task Closed(HubConnection hub)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        return closed.Task;
    }

    [Fact]
    public async Task Signing_out_closes_that_sessions_open_socket_within_a_check_and_no_other()
    {
        var who = await api.CreatePersonAsync();
        var login = await api.Client.LoginAsync(who.Username);
        login.EnsureSuccessStatusCode();
        var otherDevice = who with { Token = (await login.JsonAsync()).GetProperty("accessToken").GetString()! };
        await using var hub = await api.ConnectAsync(who);
        await using var otherHub = await api.ConnectAsync(otherDevice);
        var closed = Closed(hub);

        (await api.Client.PostAsync(who, "/auth/logout")).EnsureSuccessStatusCode();

        // A SessionGuard tick per step. The client's StartAsync returns at the handshake, before the hub's OnConnectedAsync has
        // registered the connection, and under load the guard's timer may start late, so one tick is not always enough: the
        // promise is "within a check or two", as in production, where ticks keep coming.
        for (var tick = 0; tick < 10 && !closed.IsCompleted; tick++)
        {
            api.Time.Advance(TimeSpan.FromSeconds(21));
            await Task.WhenAny(closed, Task.Delay(TimeSpan.FromSeconds(1)));
        }

        Assert.True(closed.IsCompleted, "the signed-out session's socket is still open");
        Assert.Equal(HubConnectionState.Connected, otherHub.State);
    }

    [Fact]
    public async Task A_banned_users_open_socket_is_closed()
    {
        var who = await api.CreatePersonAsync();
        await using var hub = await api.ConnectAsync(who);
        var closed = Closed(hub);
        await api.WithDbAsync(db => db.Users.Where(u => u.Id == who.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Banned)));

        Assert.True(await api.Services.GetRequiredService<SessionGuard>().SweepAsync() >= 1);

        await closed.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

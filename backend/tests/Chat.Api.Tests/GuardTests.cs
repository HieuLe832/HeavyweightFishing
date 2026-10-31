using Chat.Data;
using Microsoft.EntityFrameworkCore;
using static Chat.Api.Tests.HubTestHelpers;
using static Chat.Api.Tests.SendMessageTests;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class GuardTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Command_fails_after_logout_on_an_open_connection()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        var chat = await api.OpenDirectAsync(alice, bob);
        await using var hub = await api.ConnectAsync(alice);
        await SendAsync(hub, chat, "before");

        (await api.Client.PostAsync(alice, "/auth/logout")).EnsureSuccessStatusCode();

        await AssertHubErrorAsync(() => SendAsync(hub, chat, "after"), "session_ended");
    }

    [Fact]
    public async Task Command_fails_after_the_user_is_banned()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        var chat = await api.OpenDirectAsync(alice, bob);
        await using var hub = await api.ConnectAsync(alice);

        await api.WithDbAsync(db => db.Users.Where(u => u.Id == alice.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Banned)));

        await AssertHubErrorAsync(() => SendAsync(hub, chat, "after"), "session_ended");
    }

    [Fact]
    public async Task Twenty_sends_pass_and_the_21st_is_rate_limited_until_the_window_passes()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        var chat = await api.OpenDirectAsync(alice, bob);
        await using var hub = await api.ConnectAsync(alice);

        for (var i = 0; i < 20; i++) await SendAsync(hub, chat, $"m{i}");
        await AssertHubErrorAsync(() => SendAsync(hub, chat, "21st"), "rate_limited");

        api.Time.Advance(TimeSpan.FromSeconds(11));
        var sent = await SendAsync(hub, chat, "later");
        Assert.Equal(21, sent.GetProperty("seq").GetInt64());
    }

    [Fact]
    public async Task The_rate_limit_is_per_user()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        var chat = await api.OpenDirectAsync(alice, bob);
        await using var aliceHub = await api.ConnectAsync(alice);
        await using var bobHub = await api.ConnectAsync(bob);
        for (var i = 0; i < 20; i++) await SendAsync(aliceHub, chat, $"m{i}");

        var sent = await SendAsync(bobHub, chat, "my turn");

        Assert.Equal(21, sent.GetProperty("seq").GetInt64());
    }
}

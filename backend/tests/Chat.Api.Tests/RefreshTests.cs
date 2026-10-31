using System.Net;
using System.Net.Http.Json;
using Chat.Data;
using Microsoft.EntityFrameworkCore;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class RefreshTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static async Task AssertInvalidRefreshAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_refresh_token", (await response.JsonAsync()).GetProperty("code").GetString());
    }

    private Task<List<Chat.Data.Entities.Session>> SessionsOfAsync(TestUser user) => api.WithDbAsync(db => db.Sessions
        .Where(s => db.Users.Any(u => u.Id == s.UserId && u.Email == user.Email)).ToListAsync());

    private async Task<(TestUser User, Tokens Tokens)> SignedInAsync()
    {
        var user = await api.CreateActiveUserAsync();
        return (user, await api.SignInAsync(user));
    }

    [Fact]
    public async Task Refresh_rotates_the_token_inside_the_same_session()
    {
        var (user, first) = await SignedInAsync();
        api.Time.Advance(TimeSpan.FromDays(1));

        var response = await api.Client.RefreshAsync(first.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        var newRefresh = body.GetProperty("refreshToken").GetString()!;
        Assert.NotEqual(first.RefreshToken, newRefresh);
        Assert.Equal(HttpStatusCode.OK,
            (await api.Client.GetAuthedAsync("/auth/me", body.GetProperty("accessToken").GetString()!)).StatusCode);

        var session = Assert.Single(await SessionsOfAsync(user));
        Assert.Equal(api.Time.GetUtcNow(), session.LastSeenAt);
        Assert.Equal(api.Time.GetUtcNow().AddDays(30), session.ExpiresAt);
        var tokens = await api.WithDbAsync(db => db.RefreshTokens.Where(t => t.SessionId == session.Id).ToListAsync());
        Assert.Equal(2, tokens.Count);
        Assert.Single(tokens, t => t.UsedAt is not null);
    }

    [Fact]
    public async Task The_new_refresh_token_can_be_used_in_turn()
    {
        var (_, first) = await SignedInAsync();
        var second = (await (await api.Client.RefreshAsync(first.RefreshToken)).JsonAsync())
            .GetProperty("refreshToken").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await api.Client.RefreshAsync(second)).StatusCode);
    }

    [Fact]
    public async Task Reusing_a_used_refresh_token_revokes_the_whole_session()
    {
        var (user, first) = await SignedInAsync();
        var rotated = await (await api.Client.RefreshAsync(first.RefreshToken)).JsonAsync();
        var newRefresh = rotated.GetProperty("refreshToken").GetString()!;
        var newAccess = rotated.GetProperty("accessToken").GetString()!;

        await AssertInvalidRefreshAsync(await api.Client.RefreshAsync(first.RefreshToken));

        var session = Assert.Single(await SessionsOfAsync(user));
        Assert.NotNull(session.RevokedAt);
        Assert.Equal("refresh_reuse", session.RevokeReason);
        await AssertInvalidRefreshAsync(await api.Client.RefreshAsync(newRefresh));
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client.GetAuthedAsync("/auth/me", newAccess)).StatusCode);
    }

    [Fact]
    public async Task Two_parallel_refreshes_with_one_token_let_only_one_through()
    {
        var (_, first) = await SignedInAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => api.NewClient().RefreshAsync(first.RefreshToken)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task An_unknown_refresh_token_is_rejected()
    {
        await AssertInvalidRefreshAsync(await api.Client.RefreshAsync("not-a-real-token"));
    }

    [Fact]
    public async Task A_missing_refresh_token_is_a_validation_error()
    {
        var response = await api.Client.PostAsJsonAsync("/auth/refresh", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_session_cannot_be_refreshed()
    {
        var (_, first) = await SignedInAsync();
        api.Time.Advance(TimeSpan.FromDays(31));

        await AssertInvalidRefreshAsync(await api.Client.RefreshAsync(first.RefreshToken));
    }

    [Fact]
    public async Task A_banned_user_cannot_refresh_and_the_session_is_revoked()
    {
        var (user, first) = await SignedInAsync();
        await api.WithDbAsync(db => db.Users.Where(u => u.Email == user.Email)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Banned)));

        await AssertInvalidRefreshAsync(await api.Client.RefreshAsync(first.RefreshToken));

        var session = Assert.Single(await SessionsOfAsync(user));
        Assert.Equal("user_inactive", session.RevokeReason);
    }

    [Fact]
    public async Task Refresh_reads_the_role_again()
    {
        var (user, first) = await SignedInAsync();
        await api.WithDbAsync(db => db.Users.Where(u => u.Email == user.Email)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, UserRole.Admin)));

        var body = await (await api.Client.RefreshAsync(first.RefreshToken)).JsonAsync();

        Assert.Equal("admin", body.GetProperty("user").GetProperty("role").GetString());
        var adminRoute = await api.Client.GetAuthedAsync("/test/admin", body.GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.OK, adminRoute.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_session_and_its_tokens_stop_working()
    {
        var (user, tokens) = await SignedInAsync();

        var response = await api.Client.SendAsync(Authed(HttpMethod.Post, "/auth/logout", tokens.AccessToken));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var session = Assert.Single(await SessionsOfAsync(user));
        Assert.Equal("logout", session.RevokeReason);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client.GetAuthedAsync("/auth/me", tokens.AccessToken)).StatusCode);
        await AssertInvalidRefreshAsync(await api.Client.RefreshAsync(tokens.RefreshToken));
    }

    [Fact]
    public async Task Logout_needs_a_token()
    {
        var response = await api.Client.PostAsync("/auth/logout", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Sessions_lists_the_live_devices_and_marks_the_current_one()
    {
        var user = await api.CreateActiveUserAsync();
        var phone = await api.SignInAsync(user, "Phone");
        var laptop = await api.SignInAsync(user, "Laptop");
        var gone = await api.SignInAsync(user, "Old tablet");
        (await api.Client.SendAsync(Authed(HttpMethod.Post, "/auth/logout", gone.AccessToken))).EnsureSuccessStatusCode();

        var response = await api.Client.GetAuthedAsync("/auth/sessions", laptop.AccessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = (await response.JsonAsync()).EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal(["Laptop", "Phone"], list.Select(s => s.GetProperty("deviceName").GetString()).Order());
        var current = Assert.Single(list, s => s.GetProperty("current").GetBoolean());
        Assert.Equal("Laptop", current.GetProperty("deviceName").GetString());
        Assert.NotNull(phone);
    }
}

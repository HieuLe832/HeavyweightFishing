using System.Net;
using Chat.Data;
using Microsoft.EntityFrameworkCore;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class AuthorizeTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task A_valid_token_reaches_a_protected_route_as_that_user()
    {
        var user = await api.CreateActiveUserAsync();
        var tokens = await api.SignInAsync(user);

        var response = await api.Client.GetAuthedAsync("/auth/me", tokens.AccessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal(user.Username, body.GetProperty("username").GetString());
        Assert.Equal(user.Email, body.GetProperty("email").GetString());
        Assert.Equal("user", body.GetProperty("role").GetString());
    }

    [Fact]
    public async Task No_token_is_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client.GetAsync("/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_token_with_a_broken_signature_is_401()
    {
        var tokens = await api.SignInAsync(await api.CreateActiveUserAsync());
        var broken = tokens.AccessToken[..^3] + (tokens.AccessToken.EndsWith("AAA") ? "BBB" : "AAA");

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client.GetAuthedAsync("/auth/me", broken)).StatusCode);
    }

    [Fact]
    public async Task An_expired_access_token_is_401()
    {
        var tokens = await api.SignInAsync(await api.CreateActiveUserAsync());
        api.Time.Advance(TimeSpan.FromMinutes(16));

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client.GetAuthedAsync("/auth/me", tokens.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task A_revoked_session_stops_working_at_once()
    {
        var user = await api.CreateActiveUserAsync();
        var tokens = await api.SignInAsync(user);
        await api.WithDbAsync(db => db.Sessions
            .Where(s => db.Users.Any(u => u.Id == s.UserId && u.Email == user.Email))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, api.Time.GetUtcNow())));

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client.GetAuthedAsync("/auth/me", tokens.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task An_expired_session_is_401_even_with_a_live_access_token()
    {
        var user = await api.CreateActiveUserAsync();
        var tokens = await api.SignInAsync(user);
        await api.WithDbAsync(db => db.Sessions
            .Where(s => db.Users.Any(u => u.Id == s.UserId && u.Email == user.Email))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, api.Time.GetUtcNow().AddSeconds(-1))));

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client.GetAuthedAsync("/auth/me", tokens.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task An_admin_token_carries_the_admin_role_and_a_user_token_does_not()
    {
        var admin = await api.CreateActiveUserAsync();
        await api.WithDbAsync(db => db.Users.Where(u => u.Email == admin.Email)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, UserRole.Admin)));
        var user = await api.CreateActiveUserAsync();

        var adminResponse = await api.Client.GetAuthedAsync("/test/admin", (await api.SignInAsync(admin)).AccessToken);
        var userResponse = await api.Client.GetAuthedAsync("/test/admin", (await api.SignInAsync(user)).AccessToken);

        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, userResponse.StatusCode);
    }
}

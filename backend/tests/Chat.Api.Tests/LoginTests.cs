using System.Net;
using System.Net.Http.Json;
using Chat.Data;
using Microsoft.EntityFrameworkCore;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class LoginTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.JsonAsync()).GetProperty("code").GetString());
    }

    private Task SetStatusAsync(string email, UserStatus status) => api.WithDbAsync(db =>
        db.Users.Where(u => u.Email == email).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, status)));

    [Fact]
    public async Task Login_returns_tokens_and_creates_a_session_for_the_device()
    {
        var user = await api.CreateActiveUserAsync();

        var response = await api.Client.LoginAsync(user.Username, deviceName: "Lan's phone");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("accessToken").GetString()));
        Assert.Equal(15 * 60, body.GetProperty("expiresIn").GetInt32());
        var refresh = body.GetProperty("refreshToken").GetString()!;
        Assert.Equal(user.Username, body.GetProperty("user").GetProperty("username").GetString());
        Assert.Equal("user", body.GetProperty("user").GetProperty("role").GetString());

        var sessions = await api.WithDbAsync(db => db.Sessions
            .Where(s => db.Users.Any(u => u.Id == s.UserId && u.Email == user.Email)).ToListAsync());
        var session = Assert.Single(sessions);
        Assert.Equal("Lan's phone", session.DeviceName);
        Assert.Null(session.RevokedAt);
        Assert.Equal(api.Time.GetUtcNow().AddDays(30), session.ExpiresAt);
        var token = await api.WithDbAsync(db => db.RefreshTokens.SingleAsync(t => t.SessionId == session.Id));
        Assert.NotEqual(refresh, Convert.ToBase64String(token.TokenHash));
        Assert.Equal(api.Time.GetUtcNow().AddDays(30), token.ExpiresAt);
    }

    [Fact]
    public async Task Login_accepts_the_email_in_any_case_and_defaults_the_device_name()
    {
        var user = await api.CreateActiveUserAsync();

        var response = await api.Client.LoginAsync(user.Email.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await api.WithDbAsync(db => db.Sessions
            .SingleAsync(s => db.Users.Any(u => u.Id == s.UserId && u.Email == user.Email)));
        Assert.Equal("Unknown device", session.DeviceName);
        Assert.NotNull(session.IpAddress);
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_user_get_the_same_answer()
    {
        var user = await api.CreateActiveUserAsync();

        var wrong = await api.Client.LoginAsync(user.Username, "not-the-password");
        var unknown = await api.Client.LoginAsync(Unique(), Password);

        await AssertErrorAsync(wrong, HttpStatusCode.Unauthorized, "invalid_credentials");
        await AssertErrorAsync(unknown, HttpStatusCode.Unauthorized, "invalid_credentials");
    }

    [Fact]
    public async Task A_pending_account_with_the_right_password_is_told_to_verify_its_email()
    {
        var username = Unique();
        var email = Unique() + "@example.com";
        (await api.Client.RegisterAsync(username, email)).EnsureSuccessStatusCode();

        await AssertErrorAsync(await api.Client.LoginAsync(username), HttpStatusCode.Forbidden, "email_not_verified");
        await AssertErrorAsync(await api.Client.LoginAsync(username, "wrong-password"),
            HttpStatusCode.Unauthorized, "invalid_credentials");
    }

    [Fact]
    public async Task A_banned_account_cannot_sign_in()
    {
        var user = await api.CreateActiveUserAsync();
        await SetStatusAsync(user.Email, UserStatus.Banned);

        await AssertErrorAsync(await api.Client.LoginAsync(user.Username), HttpStatusCode.Forbidden, "account_banned");
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_against_the_right_one()
    {
        var user = await api.CreateActiveUserAsync();
        for (var i = 0; i < 5; i++) await api.Client.LoginAsync(user.Username, "wrong-password");

        await AssertErrorAsync(await api.Client.LoginAsync(user.Username), HttpStatusCode.Unauthorized, "invalid_credentials");
        var locked = await api.WithDbAsync(db => db.Users.SingleAsync(u => u.Email == user.Email));
        Assert.Equal(api.Time.GetUtcNow().AddMinutes(15), locked.LockedUntil);
    }

    [Fact]
    public async Task The_lock_ends_after_fifteen_minutes()
    {
        var user = await api.CreateActiveUserAsync();
        for (var i = 0; i < 5; i++) await api.Client.LoginAsync(user.Username, "wrong-password");
        api.Time.Advance(TimeSpan.FromMinutes(16));

        var response = await api.Client.LoginAsync(user.Username);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await api.WithDbAsync(db => db.Users.SingleAsync(u => u.Email == user.Email));
        Assert.Null(after.LockedUntil);
        Assert.Equal(0, after.FailedLoginCount);
    }

    [Fact]
    public async Task A_successful_login_resets_the_failure_count()
    {
        var user = await api.CreateActiveUserAsync();
        for (var i = 0; i < 4; i++) await api.Client.LoginAsync(user.Username, "wrong-password");
        (await api.Client.LoginAsync(user.Username)).EnsureSuccessStatusCode();
        for (var i = 0; i < 4; i++) await api.Client.LoginAsync(user.Username, "wrong-password");

        Assert.Equal(HttpStatusCode.OK, (await api.Client.LoginAsync(user.Username)).StatusCode);
    }

    [Fact]
    public async Task Login_rejects_a_missing_password()
    {
        var response = await api.Client.PostAsJsonAsync("/auth/login", new { identifier = "someone" });

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "validation_failed");
    }
}

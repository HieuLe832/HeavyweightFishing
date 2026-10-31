using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class PasswordResetTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string NewPassword = "a-brand-new-password";

    private Task<HttpResponseMessage> RequestAsync(string email) =>
        api.Client.PostAsJsonAsync("/auth/password-reset/request", new { email });

    private Task<HttpResponseMessage> ConfirmAsync(string email, string code, string newPassword = NewPassword) =>
        api.Client.PostAsJsonAsync("/auth/password-reset/confirm", new { email, code, newPassword });

    private static async Task AssertInvalidCodeAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_code", (await response.JsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Requesting_a_reset_for_an_active_account_emails_a_code()
    {
        var user = await api.CreateActiveUserAsync();
        var before = api.Email.To(user.Email).Count;

        var response = await RequestAsync(user.Email);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(before + 1, api.Email.To(user.Email).Count);
        Assert.Contains("reset", api.Email.To(user.Email).Last().Subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Requesting_a_reset_for_an_unknown_or_pending_account_sends_nothing_but_answers_202()
    {
        var unknown = Unique() + "@example.com";
        var pending = Unique() + "@example.com";
        (await api.Client.RegisterAsync(Unique(), pending)).EnsureSuccessStatusCode();
        var before = api.Email.To(pending).Count;

        Assert.Equal(HttpStatusCode.Accepted, (await RequestAsync(unknown)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await RequestAsync(pending)).StatusCode);

        Assert.Empty(api.Email.To(unknown));
        Assert.Equal(before, api.Email.To(pending).Count);
    }

    [Fact]
    public async Task Requests_inside_the_cooldown_send_only_one_email()
    {
        var user = await api.CreateActiveUserAsync();
        var before = api.Email.To(user.Email).Count;

        await RequestAsync(user.Email);
        var second = await RequestAsync(user.Email);

        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(before + 1, api.Email.To(user.Email).Count);
    }

    [Fact]
    public async Task Confirming_sets_the_new_password()
    {
        var user = await api.CreateActiveUserAsync();
        await RequestAsync(user.Email);

        var response = await ConfirmAsync(user.Email, api.Email.LastCodeFor(user.Email));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Client.LoginAsync(user.Username, NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client.LoginAsync(user.Username, user.Password)).StatusCode);
    }

    [Fact]
    public async Task Confirming_signs_the_account_out_everywhere()
    {
        var user = await api.CreateActiveUserAsync();
        var phone = await api.SignInAsync(user, "Phone");
        var laptop = await api.SignInAsync(user, "Laptop");
        await RequestAsync(user.Email);

        (await ConfirmAsync(user.Email, api.Email.LastCodeFor(user.Email))).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client.GetAuthedAsync("/auth/me", phone.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client.RefreshAsync(laptop.RefreshToken)).StatusCode);
        var sessions = await api.WithDbAsync(db => db.Sessions
            .Where(s => db.Users.Any(u => u.Id == s.UserId && u.Email == user.Email)).ToListAsync());
        Assert.All(sessions, s => Assert.Equal("password_reset", s.RevokeReason));
    }

    [Fact]
    public async Task Confirming_lifts_a_lockout()
    {
        var user = await api.CreateActiveUserAsync();
        for (var i = 0; i < 5; i++) await api.Client.LoginAsync(user.Username, "wrong-password");
        await RequestAsync(user.Email);

        (await ConfirmAsync(user.Email, api.Email.LastCodeFor(user.Email))).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await api.Client.LoginAsync(user.Username, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task A_wrong_code_changes_nothing()
    {
        var user = await api.CreateActiveUserAsync();
        await RequestAsync(user.Email);
        var right = api.Email.LastCodeFor(user.Email);

        await AssertInvalidCodeAsync(await ConfirmAsync(user.Email, right == "000000" ? "111111" : "000000"));

        Assert.Equal(HttpStatusCode.OK, (await api.Client.LoginAsync(user.Username, user.Password)).StatusCode);
    }

    [Fact]
    public async Task A_reset_code_works_only_once()
    {
        var user = await api.CreateActiveUserAsync();
        await RequestAsync(user.Email);
        var code = api.Email.LastCodeFor(user.Email);
        (await ConfirmAsync(user.Email, code)).EnsureSuccessStatusCode();

        await AssertInvalidCodeAsync(await ConfirmAsync(user.Email, code, "yet-another-password"));
    }

    [Fact]
    public async Task Confirming_for_an_unknown_email_gets_the_invalid_code_answer()
    {
        await AssertInvalidCodeAsync(await ConfirmAsync(Unique() + "@example.com", "123456"));
    }

    [Fact]
    public async Task A_weak_new_password_is_rejected_without_using_up_the_code()
    {
        var user = await api.CreateActiveUserAsync();
        await RequestAsync(user.Email);
        var code = api.Email.LastCodeFor(user.Email);

        var weak = await ConfirmAsync(user.Email, code, "short");

        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Equal("validation_failed", (await weak.JsonAsync()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmAsync(user.Email, code)).StatusCode);
    }
}

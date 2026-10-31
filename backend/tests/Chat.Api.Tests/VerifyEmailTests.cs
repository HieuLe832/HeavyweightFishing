using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class VerifyEmailTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private async Task<string> RegisterPendingAsync()
    {
        var email = Unique() + "@example.com";
        (await api.Client.RegisterAsync(Unique(), email)).EnsureSuccessStatusCode();
        return email;
    }

    private Task<Chat.Data.Entities.User> UserAsync(string email) =>
        api.WithDbAsync(db => db.Users.SingleAsync(u => u.Email == email));

    private static async Task AssertInvalidCodeAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_code", (await response.JsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_right_code_activates_the_account()
    {
        var email = await RegisterPendingAsync();

        var response = await api.Client.VerifyEmailAsync(email, api.Email.LastCodeFor(email));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var user = await UserAsync(email);
        Assert.Equal(Chat.Data.UserStatus.Active, user.Status);
        Assert.Equal(api.Time.GetUtcNow(), user.EmailVerifiedAt);
    }

    [Fact]
    public async Task A_wrong_code_is_rejected_and_the_account_stays_pending()
    {
        var email = await RegisterPendingAsync();
        var wrong = api.Email.LastCodeFor(email) == "000000" ? "111111" : "000000";

        await AssertInvalidCodeAsync(await api.Client.VerifyEmailAsync(email, wrong));

        Assert.Equal(Chat.Data.UserStatus.Pending, (await UserAsync(email)).Status);
    }

    [Fact]
    public async Task Five_wrong_guesses_kill_the_code_even_for_the_right_one()
    {
        var email = await RegisterPendingAsync();
        var right = api.Email.LastCodeFor(email);
        var wrong = right == "000000" ? "111111" : "000000";
        for (var i = 0; i < 5; i++) await api.Client.VerifyEmailAsync(email, wrong);

        await AssertInvalidCodeAsync(await api.Client.VerifyEmailAsync(email, right));

        Assert.Equal(Chat.Data.UserStatus.Pending, (await UserAsync(email)).Status);
    }

    [Fact]
    public async Task An_expired_code_is_rejected()
    {
        var email = await RegisterPendingAsync();
        var code = api.Email.LastCodeFor(email);
        api.Time.Advance(TimeSpan.FromMinutes(16));

        await AssertInvalidCodeAsync(await api.Client.VerifyEmailAsync(email, code));
    }

    [Fact]
    public async Task A_code_works_only_once()
    {
        var email = await RegisterPendingAsync();
        var code = api.Email.LastCodeFor(email);
        (await api.Client.VerifyEmailAsync(email, code)).EnsureSuccessStatusCode();

        await AssertInvalidCodeAsync(await api.Client.VerifyEmailAsync(email, code));
    }

    [Fact]
    public async Task An_unknown_email_gets_the_same_invalid_code_answer()
    {
        await AssertInvalidCodeAsync(await api.Client.VerifyEmailAsync(Unique() + "@example.com", "123456"));
    }

    [Fact]
    public async Task Resending_supersedes_the_old_code_with_a_new_one()
    {
        var email = await RegisterPendingAsync();
        var oldCode = api.Email.LastCodeFor(email);
        api.Time.Advance(TimeSpan.FromSeconds(61));

        var resend = await api.Client.PostAsJsonAsync("/auth/resend-verification", new { email });

        Assert.Equal(HttpStatusCode.Accepted, resend.StatusCode);
        Assert.Equal(2, api.Email.To(email).Count);
        var newCode = api.Email.LastCodeFor(email);
        if (newCode != oldCode) await AssertInvalidCodeAsync(await api.Client.VerifyEmailAsync(email, oldCode));
        Assert.Equal(HttpStatusCode.NoContent, (await api.Client.VerifyEmailAsync(email, newCode)).StatusCode);
    }

    [Fact]
    public async Task Resending_within_the_cooldown_sends_nothing_but_still_answers_202()
    {
        var email = await RegisterPendingAsync();

        var resend = await api.Client.PostAsJsonAsync("/auth/resend-verification", new { email });

        Assert.Equal(HttpStatusCode.Accepted, resend.StatusCode);
        Assert.Single(api.Email.To(email));
    }

    [Fact]
    public async Task Resending_for_an_unknown_or_active_account_sends_nothing_but_answers_202()
    {
        var unknown = Unique() + "@example.com";
        var active = await api.CreateActiveUserAsync();
        var before = api.Email.To(active.Email).Count;
        api.Time.Advance(TimeSpan.FromSeconds(61));

        var r1 = await api.Client.PostAsJsonAsync("/auth/resend-verification", new { email = unknown });
        var r2 = await api.Client.PostAsJsonAsync("/auth/resend-verification", new { email = active.Email });

        Assert.Equal(HttpStatusCode.Accepted, r1.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, r2.StatusCode);
        Assert.Empty(api.Email.To(unknown));
        Assert.Equal(before, api.Email.To(active.Email).Count);
    }
}

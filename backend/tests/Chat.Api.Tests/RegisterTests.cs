using System.Net;
using Microsoft.EntityFrameworkCore;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class RegisterTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Register_creates_a_pending_account_and_emails_a_code()
    {
        var username = Unique();
        var email = Unique() + "@example.com";

        var response = await api.Client.RegisterAsync(username, email);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var userId = (await response.JsonAsync()).GetProperty("userId").GetGuid();
        var user = await api.WithDbAsync(db => db.Users.SingleAsync(u => u.Id == userId));
        Assert.Equal(Chat.Data.UserStatus.Pending, user.Status);
        Assert.Null(user.EmailVerifiedAt);
        Assert.NotNull(user.PasswordHash);
        Assert.DoesNotContain(Password, user.PasswordHash);
        Assert.Equal(username, user.DisplayName);
        var code = api.Email.LastCodeFor(email);
        var stored = await api.WithDbAsync(db => db.VerificationCodes.SingleAsync(c => c.UserId == userId));
        Assert.NotEqual(code, stored.CodeHash);
        Assert.Equal(Chat.Data.VerificationPurpose.VerifyEmail, stored.Purpose);
    }

    [Fact]
    public async Task Register_uses_the_given_display_name()
    {
        var response = await api.Client.RegisterAsync(Unique(), Unique() + "@example.com", displayName: "Lan Nguyen");

        var userId = (await response.JsonAsync()).GetProperty("userId").GetGuid();
        var user = await api.WithDbAsync(db => db.Users.SingleAsync(u => u.Id == userId));
        Assert.Equal("Lan Nguyen", user.DisplayName);
    }

    [Fact]
    public async Task Register_rejects_a_taken_email_ignoring_case()
    {
        var email = Unique() + "@example.com";
        (await api.Client.RegisterAsync(Unique(), email)).EnsureSuccessStatusCode();

        var response = await api.Client.RegisterAsync(Unique(), email.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("email_taken", (await response.JsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Register_rejects_a_taken_username_ignoring_case()
    {
        var username = Unique();
        (await api.Client.RegisterAsync(username, Unique() + "@example.com")).EnsureSuccessStatusCode();

        var response = await api.Client.RegisterAsync(username.ToUpperInvariant(), Unique() + "@example.com");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("username_taken", (await response.JsonAsync()).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("ab", "a@example.com", "correct-horse-1")]            // username too short
    [InlineData("has space", "a@example.com", "correct-horse-1")]     // username characters
    [InlineData("valid_name", "not-an-email", "correct-horse-1")]     // email syntax
    [InlineData("valid_name", "a@example.com", "short")]              // password too short
    public async Task Register_rejects_invalid_input(string username, string email, string password)
    {
        var response = await api.Client.RegisterAsync(username, email, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", (await response.JsonAsync()).GetProperty("code").GetString());
        Assert.Empty(api.Email.To(email));
    }
}

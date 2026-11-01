using System.Net;
using System.Net.Http.Json;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

/// <summary>The API with a small per-address limit on the anonymous auth endpoints (the shared fixture sets it out of reach).</summary>
public class AuthRateLimitFixture : ApiFixture
{
    protected override Task<Dictionary<string, string?>> SettingsAsync() =>
        Task.FromResult(new Dictionary<string, string?> { ["Auth:IpLimitCount"] = "3", ["Auth:IpLimitWindowSeconds"] = "3600" });
}

public class AuthRateLimitTests(AuthRateLimitFixture api) : IClassFixture<AuthRateLimitFixture>
{
    [Fact]
    public async Task The_anonymous_auth_endpoints_share_a_per_address_limit_and_signed_in_calls_do_not_count()
    {
        // Signed in before the window fills; the test server gives every request the same address.
        var who = await api.CreatePersonAsync(); // register, verify and log in: the three allowed calls

        await (await api.Client.LoginAsync("nobody-" + Unique(), "wrong-password")).AssertErrorAsync((HttpStatusCode)429, "rate_limited");
        await (await api.Client.PostAsJsonAsync("/auth/password-reset/request", new { email = "x@example.test" }))
            .AssertErrorAsync((HttpStatusCode)429, "rate_limited");

        Assert.Equal(HttpStatusCode.OK, (await api.Client.GetAsync(who, "/auth/me")).StatusCode);
    }
}

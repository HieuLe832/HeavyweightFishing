using System.Net.Http.Json;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

/// <summary>The API behind a trusted proxy network (the test server's requests have no address of their own).</summary>
public class TrustedProxyFixture : ApiFixture
{
    protected override Task<Dictionary<string, string?>> SettingsAsync() =>
        Task.FromResult(new Dictionary<string, string?> { ["ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/8" });
}

internal static class ForwardedLogin
{
    /// <summary>Signs in with an X-Forwarded-For header and returns the address the new session recorded.</summary>
    public static async Task<string> SessionAddressAsync(ApiFixture api, string forwardedFor)
    {
        var user = await api.CreateActiveUserAsync();
        using var login = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        {
            Content = JsonContent.Create(new { identifier = user.Username, password = user.Password }),
        };
        login.Headers.Add("X-Forwarded-For", forwardedFor);
        var response = await api.Client.SendAsync(login);
        response.EnsureSuccessStatusCode();
        var token = (await response.JsonAsync()).GetProperty("accessToken").GetString()!;

        var sessions = await (await api.Client.GetAuthedAsync("/auth/sessions", token)).JsonAsync();
        return sessions.EnumerateArray().Single(s => s.GetProperty("current").GetBoolean()).GetProperty("ipAddress").GetString()!;
    }
}

public class ForwardedHeadersIgnoredTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Without_trusted_proxies_a_forwarded_address_is_ignored() =>
        Assert.NotEqual("203.0.113.7", await ForwardedLogin.SessionAddressAsync(api, "203.0.113.7"));
}

public class ForwardedHeadersTrustedTests(TrustedProxyFixture api) : IClassFixture<TrustedProxyFixture>
{
    [Fact]
    public async Task Behind_a_trusted_proxy_the_session_records_the_client_address() =>
        Assert.Equal("203.0.113.7", await ForwardedLogin.SessionAddressAsync(api, "203.0.113.7"));
}

using System.Net;
using System.Net.Http.Headers;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class CorsApiFixture : ApiFixture
{
    protected override Task<Dictionary<string, string?>> SettingsAsync() =>
        Task.FromResult(new Dictionary<string, string?> { ["Cors:AllowedOrigins:0"] = "http://localhost:3000" });
}

public class CorsTests(CorsApiFixture api) : IClassFixture<CorsApiFixture>
{
    private const string Allowed = "http://localhost:3000";

    private static HttpRequestMessage Preflight(string path, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,x-signalr-user-agent");
        return request;
    }

    private static string? AllowOrigin(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) ? values.Single() : null;

    [Fact]
    public async Task A_preflight_from_an_allowed_origin_gets_the_cors_headers()
    {
        var response = await api.Client.SendAsync(Preflight("/conversations", Allowed));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(Allowed, AllowOrigin(response));
        Assert.Contains("authorization", string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_rest_call_from_an_allowed_origin_gets_the_allow_origin_header()
    {
        var person = await api.CreatePersonAsync();
        var request = Authed(HttpMethod.Get, "/conversations", person.Token);
        request.Headers.Add("Origin", Allowed);

        var response = await api.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Allowed, AllowOrigin(response));
    }

    [Fact]
    public async Task A_hub_negotiate_from_an_allowed_origin_gets_the_allow_origin_header()
    {
        var person = await api.CreatePersonAsync();
        var request = Authed(HttpMethod.Post, "/hubs/chat/negotiate?negotiateVersion=1", person.Token);
        request.Headers.Add("Origin", Allowed);

        var response = await api.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Allowed, AllowOrigin(response));
    }

    [Fact]
    public async Task Another_origin_gets_no_allow_origin_header()
    {
        var response = await api.Client.SendAsync(Preflight("/conversations", "http://evil.example"));

        Assert.Null(AllowOrigin(response));
    }
}

public class CorsDisabledTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task With_no_allowed_origins_nobody_gets_the_header()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/conversations");
        request.Headers.Add("Origin", "http://localhost:3000");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await api.Client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}

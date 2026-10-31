using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Chat.Api.Tests;

public static class TestHelpers
{
    public const string Password = "correct-horse-1";

    public static string Unique(string prefix = "u") => prefix + Guid.NewGuid().ToString("N")[..10];

    public static Task<HttpResponseMessage> RegisterAsync(this HttpClient c, string username, string email,
        string password = Password, string? displayName = null) =>
        c.PostAsJsonAsync("/auth/register", new { username, email, password, displayName });

    public static Task<HttpResponseMessage> VerifyEmailAsync(this HttpClient c, string email, string code) =>
        c.PostAsJsonAsync("/auth/verify-email", new { email, code });

    public static Task<HttpResponseMessage> LoginAsync(this HttpClient c, string identifier,
        string password = Password, string? deviceName = null) =>
        c.PostAsJsonAsync("/auth/login", new { identifier, password, deviceName });

    public static Task<HttpResponseMessage> RefreshAsync(this HttpClient c, string refreshToken) =>
        c.PostAsJsonAsync("/auth/refresh", new { refreshToken });

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>());

    public static HttpRequestMessage Authed(HttpMethod method, string url, string accessToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    public static Task<HttpResponseMessage> GetAuthedAsync(this HttpClient c, string url, string accessToken) =>
        c.SendAsync(Authed(HttpMethod.Get, url, accessToken));

    public record TestUser(string Username, string Email, string Password = Password);

    /// <summary>Registers and verifies an account so it can sign in.</summary>
    public static async Task<TestUser> CreateActiveUserAsync(this ApiFixture api)
    {
        var user = new TestUser(Unique(), Unique() + "@example.com");
        (await api.Client.RegisterAsync(user.Username, user.Email)).EnsureSuccessStatusCode();
        (await api.Client.VerifyEmailAsync(user.Email, api.Email.LastCodeFor(user.Email))).EnsureSuccessStatusCode();
        return user;
    }

    public record Tokens(string AccessToken, string RefreshToken, JsonElement Body);

    public static async Task<Tokens> SignInAsync(this ApiFixture api, TestUser user, string? deviceName = null)
    {
        var response = await api.Client.LoginAsync(user.Username, user.Password, deviceName);
        response.EnsureSuccessStatusCode();
        var body = await response.JsonAsync();
        return new Tokens(body.GetProperty("accessToken").GetString()!,
            body.GetProperty("refreshToken").GetString()!, body);
    }

    /// <summary>A verified, signed-in account: its id, username and access token.</summary>
    public record Person(Guid Id, string Username, string Token);

    public static async Task<Person> CreatePersonAsync(this ApiFixture api, string? displayName = null)
    {
        var user = new TestUser(Unique(), Unique() + "@example.com");
        (await api.Client.RegisterAsync(user.Username, user.Email, displayName: displayName)).EnsureSuccessStatusCode();
        (await api.Client.VerifyEmailAsync(user.Email, api.Email.LastCodeFor(user.Email))).EnsureSuccessStatusCode();
        var tokens = await api.SignInAsync(user);
        return new Person(tokens.Body.GetProperty("user").GetProperty("id").GetGuid(), user.Username, tokens.AccessToken);
    }

    public static Task<HttpResponseMessage> SendAsync(this HttpClient c, Person who, HttpMethod method, string url,
        object? body = null)
    {
        var request = Authed(method, url, who.Token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return c.SendAsync(request);
    }

    public static Task<HttpResponseMessage> GetAsync(this HttpClient c, Person who, string url) =>
        c.SendAsync(who, HttpMethod.Get, url);

    public static Task<HttpResponseMessage> PutAsync(this HttpClient c, Person who, string url, object? body = null) =>
        c.SendAsync(who, HttpMethod.Put, url, body);

    public static Task<HttpResponseMessage> DeleteAsync(this HttpClient c, Person who, string url) =>
        c.SendAsync(who, HttpMethod.Delete, url);

    public static Task<HttpResponseMessage> PostAsync(this HttpClient c, Person who, string url, object? body = null) =>
        c.SendAsync(who, HttpMethod.Post, url, body);

    public static Task<HttpResponseMessage> PatchAsync(this HttpClient c, Person who, string url, object? body = null) =>
        c.SendAsync(who, HttpMethod.Patch, url, body);

    public static async Task AssertErrorAsync(this HttpResponseMessage response, System.Net.HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.JsonAsync()).GetProperty("code").GetString());
    }
}

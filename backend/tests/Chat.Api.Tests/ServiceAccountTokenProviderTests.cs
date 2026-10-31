using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Chat.Api.Push;
using Microsoft.Extensions.Time.Testing;

namespace Chat.Api.Tests;

public class ServiceAccountTokenProviderTests : IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly FakeHandler _handler = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"sa-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        _rsa.Dispose();
        File.Delete(_file);
    }

    private ServiceAccount Account() => new("proj-1", "bot@proj-1.iam.gserviceaccount.com", _rsa.ExportPkcs8PrivateKeyPem(), "https://oauth2.example.test/token");

    private ServiceAccountTokenProvider Provider() => new(new HttpClient(_handler), Account(), _time);

    private static byte[] FromB64Url(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight(s.Length + (4 - s.Length % 4) % 4, '='));

    private static string Token(string value = "ya29.token", int expiresIn = 3600) => $$"""{"access_token":"{{value}}","expires_in":{{expiresIn}},"token_type":"Bearer"}""";

    [Fact]
    public async Task It_posts_a_signed_jwt_assertion_to_the_token_uri()
    {
        _handler.Queue(HttpStatusCode.OK, Token());

        var token = await Provider().GetAsync(CancellationToken.None);

        Assert.Equal("ya29.token", token);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://oauth2.example.test/token", request.Message.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, request.Message.Method);
        var form = HttpUtility.ParseQueryString(request.Body);
        Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", form["grant_type"]);
        var parts = form["assertion"]!.Split('.');
        Assert.Equal("RS256", JsonSerializer.Deserialize<JsonElement>(FromB64Url(parts[0])).GetProperty("alg").GetString());
        var claims = JsonSerializer.Deserialize<JsonElement>(FromB64Url(parts[1]));
        Assert.Equal("bot@proj-1.iam.gserviceaccount.com", claims.GetProperty("iss").GetString());
        Assert.Equal("https://www.googleapis.com/auth/firebase.messaging", claims.GetProperty("scope").GetString());
        Assert.Equal("https://oauth2.example.test/token", claims.GetProperty("aud").GetString());
        Assert.Equal(3600, claims.GetProperty("exp").GetInt64() - claims.GetProperty("iat").GetInt64());
        Assert.Equal(_time.GetUtcNow().ToUnixTimeSeconds(), claims.GetProperty("iat").GetInt64());
        Assert.True(_rsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), FromB64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public async Task The_token_is_reused_until_five_minutes_before_it_expires_then_renewed()
    {
        _handler.Queue(HttpStatusCode.OK, Token("first")).Queue(HttpStatusCode.OK, Token("second"));
        var provider = Provider();

        Assert.Equal("first", await provider.GetAsync(CancellationToken.None));
        _time.Advance(TimeSpan.FromMinutes(54));
        Assert.Equal("first", await provider.GetAsync(CancellationToken.None));
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("second", await provider.GetAsync(CancellationToken.None));
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_request()
    {
        _handler.Queue(HttpStatusCode.OK, Token());
        var provider = Provider();

        var tokens = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => provider.GetAsync(CancellationToken.None)));

        Assert.All(tokens, t => Assert.Equal("ya29.token", t));
        Assert.Single(_handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""")]
    [InlineData(HttpStatusCode.OK, """{"token_type":"Bearer"}""")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task A_bad_answer_throws_and_nothing_is_cached(HttpStatusCode status, string body)
    {
        _handler.Queue(status, body).Queue(HttpStatusCode.OK, Token("recovered"));
        var provider = Provider();

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetAsync(CancellationToken.None));
        Assert.Equal("recovered", await provider.GetAsync(CancellationToken.None));
    }

    [Fact]
    public void Load_reads_the_service_account_file()
    {
        File.WriteAllText(_file, JsonSerializer.Serialize(new { project_id = "proj-1", client_email = "bot@x", private_key = "PEM", token_uri = "https://t.example/token" }));

        var account = ServiceAccount.Load(_file);

        Assert.Equal(new ServiceAccount("proj-1", "bot@x", "PEM", "https://t.example/token"), account);
    }

    [Fact]
    public void Load_defaults_the_token_uri_and_names_missing_fields()
    {
        File.WriteAllText(_file, JsonSerializer.Serialize(new { project_id = "proj-1", client_email = "bot@x", private_key = "PEM" }));
        Assert.Equal("https://oauth2.googleapis.com/token", ServiceAccount.Load(_file).TokenUri);

        File.WriteAllText(_file, JsonSerializer.Serialize(new { client_email = "bot@x" }));
        var e = Assert.Throws<InvalidOperationException>(() => ServiceAccount.Load(_file));
        Assert.Contains("project_id", e.Message);
        Assert.Contains("private_key", e.Message);
        Assert.DoesNotContain("client_email", e.Message);
    }

    [Fact]
    public void Load_rejects_a_file_that_is_not_json()
    {
        File.WriteAllText(_file, "not json");

        Assert.Throws<InvalidOperationException>(() => ServiceAccount.Load(_file));
    }
}

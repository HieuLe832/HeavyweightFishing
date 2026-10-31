using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chat.Api.Push;
using Chat.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Chat.Api.Tests;

public class ApnsPushSenderTests : IDisposable
{
    private static readonly PushMessage Message = new("Nguyễn Văn A", "Chào bạn 🎣", Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"), 42);

    private readonly string _keyPath = Path.Combine(Path.GetTempPath(), $"apns-{Guid.NewGuid():N}.p8");
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeHandler _handler = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));

    public ApnsPushSenderTests() => File.WriteAllText(_keyPath, _key.ExportPkcs8PrivateKeyPem());

    public void Dispose()
    {
        _key.Dispose();
        File.Delete(_keyPath);
    }

    private ApnsPushSender Sender(bool sandbox = false) => new(new HttpClient(_handler), new ApnsOptions
    {
        KeyPath = _keyPath, KeyId = "KEY123", TeamId = "TEAM456", BundleId = "vn.example.chat", Sandbox = sandbox,
    }, _time);

    private static JsonElement Decode(string jwtPart) => JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(Pad(jwtPart.Replace('-', '+').Replace('_', '/'))));

    private static string Pad(string s) => s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');

    [Fact]
    public async Task The_request_goes_to_the_device_path_with_apns_headers()
    {
        var result = await Sender().SendAsync(Message, "abc123", CancellationToken.None);

        Assert.Equal(PushResult.Sent, result);
        var request = Assert.Single(_handler.Requests).Message;
        Assert.Equal("https://api.push.apple.com/3/device/abc123", request.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(HttpVersion.Version20, request.Version);
        Assert.Equal("vn.example.chat", request.Headers.GetValues("apns-topic").Single());
        Assert.Equal("alert", request.Headers.GetValues("apns-push-type").Single());
        Assert.Equal("10", request.Headers.GetValues("apns-priority").Single());
        Assert.Equal("bearer", request.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task The_sandbox_option_uses_the_sandbox_host()
    {
        await Sender(sandbox: true).SendAsync(Message, "abc123", CancellationToken.None);

        Assert.Equal("api.sandbox.push.apple.com", _handler.Requests.Single().Message.RequestUri!.Host);
    }

    [Fact]
    public async Task The_body_carries_alert_thread_id_and_ids_with_unicode_intact()
    {
        await Sender().SendAsync(Message, "abc123", CancellationToken.None);

        var body = JsonSerializer.Deserialize<JsonElement>(_handler.Requests.Single().Body);
        var aps = body.GetProperty("aps");
        Assert.Equal("Nguyễn Văn A", aps.GetProperty("alert").GetProperty("title").GetString());
        Assert.Equal("Chào bạn 🎣", aps.GetProperty("alert").GetProperty("body").GetString());
        Assert.Equal(Message.ConversationId.ToString(), aps.GetProperty("thread-id").GetString());
        Assert.Equal(Message.ConversationId.ToString(), body.GetProperty("conversationId").GetString());
        Assert.Equal(Message.MessageId.ToString(), body.GetProperty("messageId").GetString());
        Assert.Equal("42", body.GetProperty("seq").GetString());
    }

    [Fact]
    public async Task The_jwt_has_kid_iss_and_iat_and_verifies_with_the_public_key()
    {
        await Sender().SendAsync(Message, "abc123", CancellationToken.None);

        var parts = _handler.Requests.Single().Message.Headers.Authorization!.Parameter!.Split('.');
        Assert.Equal(3, parts.Length);
        var header = Decode(parts[0]);
        Assert.Equal("ES256", header.GetProperty("alg").GetString());
        Assert.Equal("KEY123", header.GetProperty("kid").GetString());
        var claims = Decode(parts[1]);
        Assert.Equal("TEAM456", claims.GetProperty("iss").GetString());
        Assert.Equal(_time.GetUtcNow().ToUnixTimeSeconds(), claims.GetProperty("iat").GetInt64());
        var signature = Convert.FromBase64String(Pad(parts[2].Replace('-', '+').Replace('_', '/')));
        Assert.True(_key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public async Task The_jwt_is_reused_for_50_minutes_then_renewed()
    {
        var sender = Sender();
        await sender.SendAsync(Message, "abc123", CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(49));
        await sender.SendAsync(Message, "abc123", CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(2));
        await sender.SendAsync(Message, "abc123", CancellationToken.None);

        var tokens = _handler.Requests.Select(r => r.Message.Headers.Authorization!.Parameter).ToList();
        Assert.Equal(tokens[0], tokens[1]);
        Assert.NotEqual(tokens[0], tokens[2]);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "", PushResult.Sent)]
    [InlineData(HttpStatusCode.Gone, """{"reason":"Unregistered"}""", PushResult.InvalidToken)]
    [InlineData(HttpStatusCode.BadRequest, """{"reason":"BadDeviceToken"}""", PushResult.InvalidToken)]
    [InlineData(HttpStatusCode.BadRequest, """{"reason":"Unregistered"}""", PushResult.InvalidToken)]
    public async Task A_dead_token_is_reported_and_success_is_sent(HttpStatusCode status, string body, PushResult expected)
    {
        _handler.Queue(status, body);

        Assert.Equal(expected, await Sender().SendAsync(Message, "abc123", CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, """{"reason":"ExpiredProviderToken"}""", "application/json")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"reason":"TooManyRequests"}""", "application/json")]
    [InlineData(HttpStatusCode.InternalServerError, """{"reason":"InternalServerError"}""", "application/json")]
    [InlineData(HttpStatusCode.BadRequest, """{"reason":"PayloadTooLarge"}""", "application/json")]
    [InlineData(HttpStatusCode.BadGateway, "<html><body>Bad gateway</body></html>", "text/html")]
    public async Task Every_other_answer_throws_so_the_token_is_kept(HttpStatusCode status, string body, string contentType)
    {
        _handler.Queue(status, body, contentType);

        await Assert.ThrowsAsync<HttpRequestException>(() => Sender().SendAsync(Message, "abc123", CancellationToken.None));
    }

    [Fact]
    public void A_complete_apns_section_registers_the_apns_sender()
    {
        var senders = PushRegistrationTests.Build(new()
        {
            ["Push:Apns:KeyPath"] = _keyPath, ["Push:Apns:KeyId"] = "KEY123", ["Push:Apns:TeamId"] = "TEAM456", ["Push:Apns:BundleId"] = "vn.example.chat",
        }).GetServices<IPushSender>().ToList();

        Assert.IsType<ApnsPushSender>(senders.Single(s => s.Platform == PushPlatform.Apns));
        Assert.IsType<LoggingPushSender>(senders.Single(s => s.Platform == PushPlatform.Fcm));
        Assert.IsType<LoggingPushSender>(senders.Single(s => s.Platform == PushPlatform.Web));
    }

    [Fact]
    public void A_missing_apns_key_file_throws_naming_the_path()
    {
        var e = Assert.Throws<InvalidOperationException>(() => PushRegistrationTests.Build(new()
        {
            ["Push:Apns:KeyPath"] = _keyPath + ".missing", ["Push:Apns:KeyId"] = "K", ["Push:Apns:TeamId"] = "T", ["Push:Apns:BundleId"] = "B",
        }));

        Assert.Contains(".missing", e.Message);
    }
}

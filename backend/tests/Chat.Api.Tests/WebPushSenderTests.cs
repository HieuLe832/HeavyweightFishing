using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chat.Api.Push;
using Chat.Data;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;

namespace Chat.Api.Tests;

public class WebPushSenderTests
{
    private const string Endpoint = "https://push.example.test/send/abc123";
    private static readonly Chat.Api.Push.PushMessage Message = new("Nguyễn Văn A", "Chào bạn 🎣", Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"), 42);

    private readonly FakeHandler _handler = new();
    private readonly (string PublicKey, string PrivateKey) _vapid = NewVapidKeys();

    /// <summary>A VAPID key pair: the private scalar and the uncompressed public point, both base64url.</summary>
    private static (string PublicKey, string PrivateKey) NewVapidKeys()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ecdsa.ExportParameters(true);
        var point = new byte[] { 0x04 }.Concat(p.Q.X!).Concat(p.Q.Y!).ToArray();
        return (Base64UrlTextEncoder.Encode(point), Base64UrlTextEncoder.Encode(p.D!));
    }

    private WebPushSender Sender() => new(new PushServiceClient(new HttpClient(_handler)), new WebOptions { PublicKey = _vapid.PublicKey, PrivateKey = _vapid.PrivateKey, Subject = "mailto:ops@example.test" });

    /// <summary>A real browser-style subscription: an uncompressed P-256 public key and 16 random auth bytes.</summary>
    private static string Subscription(string endpoint = Endpoint)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p = ecdh.ExportParameters(false);
        var point = new byte[] { 0x04 }.Concat(p.Q.X!).Concat(p.Q.Y!).ToArray();
        return WebPushSender.Encode(endpoint, Base64UrlTextEncoder.Encode(point), Base64UrlTextEncoder.Encode(RandomNumberGenerator.GetBytes(16)));
    }

    [Fact]
    public async Task A_valid_subscription_is_posted_to_its_endpoint_with_vapid_and_an_encrypted_body()
    {
        _handler.Queue(HttpStatusCode.Created);

        var result = await Sender().SendAsync(Message, Subscription(), CancellationToken.None);

        Assert.Equal(PushResult.Sent, result);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(Endpoint, request.Message.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, request.Message.Method);
        Assert.StartsWith("vapid", request.Message.Headers.Authorization!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("aes128gcm", string.Join(",", request.Message.Content!.Headers.ContentEncoding));
        Assert.True(request.Message.Headers.Contains("TTL"));
        Assert.DoesNotContain("Chào", request.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task A_subscription_the_service_has_dropped_is_an_invalid_token(HttpStatusCode status)
    {
        _handler.Queue(status);

        Assert.Equal(PushResult.InvalidToken, await Sender().SendAsync(Message, Subscription(), CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Every_other_failure_throws_so_the_token_is_kept(HttpStatusCode status)
    {
        _handler.Queue(status);

        await Assert.ThrowsAsync<PushServiceClientException>(() => Sender().SendAsync(Message, Subscription(), CancellationToken.None));
    }

    [Fact]
    public void The_payload_is_json_with_the_title_body_and_ids()
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(WebPushSender.Payload(Message));

        Assert.Equal("Nguyễn Văn A", payload.GetProperty("title").GetString());
        Assert.Equal("Chào bạn 🎣", payload.GetProperty("body").GetString());
        Assert.Equal(Message.ConversationId.ToString(), payload.GetProperty("conversationId").GetString());
        Assert.Equal(Message.MessageId.ToString(), payload.GetProperty("messageId").GetString());
        Assert.Equal("42", payload.GetProperty("seq").GetString());
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("e30")]                                                       // {}
    [InlineData("eyJmb28iOjF9")]                                              // {"foo":1}
    [InlineData("bm90IGpzb24")]                                               // not json
    public async Task An_undecodable_token_is_an_invalid_token(string token)
    {
        Assert.Equal(PushResult.InvalidToken, await Sender().SendAsync(Message, token, CancellationToken.None));
        Assert.Empty(_handler.Requests);
    }

    [Theory]
    [InlineData("http://push.example.test/send/abc")]
    [InlineData("ftp://push.example.test/send/abc")]
    [InlineData("https://127.0.0.1/send/abc")]
    [InlineData("https://localhost/send/abc")]
    public async Task An_endpoint_that_is_not_a_public_https_address_is_an_invalid_token_and_is_never_called(string endpoint)
    {
        Assert.Equal(PushResult.InvalidToken, await Sender().SendAsync(Message, Subscription(endpoint), CancellationToken.None));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public void Encode_then_decode_round_trips()
    {
        var token = WebPushSender.Encode(Endpoint, "pub", "auth");

        Assert.DoesNotContain(' ', token);
        Assert.True(WebPushSender.TryDecode(token, out var subscription));
        Assert.Equal(Endpoint, subscription!.Endpoint);
        Assert.Equal("pub", subscription.GetKey(PushEncryptionKeyName.P256DH));
        Assert.Equal("auth", subscription.GetKey(PushEncryptionKeyName.Auth));
    }

    [Fact]
    public void A_complete_web_section_registers_the_web_sender()
    {
        var senders = PushRegistrationTests.Build(new()
        {
            ["Push:Web:PublicKey"] = _vapid.PublicKey, ["Push:Web:PrivateKey"] = _vapid.PrivateKey, ["Push:Web:Subject"] = "mailto:ops@example.test",
        }).GetServices<IPushSender>().ToList();

        Assert.IsType<WebPushSender>(senders.Single(s => s.Platform == PushPlatform.Web));
        Assert.IsType<LoggingPushSender>(senders.Single(s => s.Platform == PushPlatform.Fcm));
    }

    [Fact]
    public void A_vapid_subject_that_is_not_mailto_or_https_stops_the_start()
    {
        var e = Assert.Throws<InvalidOperationException>(() => PushRegistrationTests.Build(new()
        {
            ["Push:Web:PublicKey"] = _vapid.PublicKey, ["Push:Web:PrivateKey"] = _vapid.PrivateKey, ["Push:Web:Subject"] = "ops@example.test",
        }));

        Assert.Contains("Push:Web:Subject", e.Message);
    }
}

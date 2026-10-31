using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Chat.Data;

namespace Chat.Api.Push;

/// <summary>Sends to iOS devices through Apple's HTTP/2 API with a provider token (a .p8 key). The notification text is never logged.</summary>
public sealed class ApnsPushSender : IPushSender
{
    private readonly HttpClient _http;
    private readonly ApnsOptions _options;
    private readonly ApnsJwt _jwt;

    public ApnsPushSender(HttpClient http, ApnsOptions options, TimeProvider time)
    {
        _http = http;
        _options = options;
        var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(options.KeyPath!));
        _jwt = new ApnsJwt(key, options.KeyId!, options.TeamId!, time);
    }

    public PushPlatform Platform => PushPlatform.Apns;

    public async Task<PushResult> SendAsync(PushMessage message, string token, CancellationToken ct)
    {
        var host = _options.Sandbox ? "api.sandbox.push.apple.com" : "api.push.apple.com";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/3/device/{Uri.EscapeDataString(token)}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        // `thread-id` is not a valid C# member name, so the body is built as a dictionary.
        request.Content = JsonContent.Create(new Dictionary<string, object>
        {
            ["aps"] = new Dictionary<string, object>
            {
                ["alert"] = new { title = message.Title, body = message.Body },
                ["sound"] = "default",
                ["thread-id"] = message.ConversationId.ToString(),
            },
            ["conversationId"] = message.ConversationId.ToString(),
            ["messageId"] = message.MessageId.ToString(),
            ["seq"] = message.Seq.ToString(),
        });
        request.Headers.Authorization = new("bearer", _jwt.Token());
        request.Headers.Add("apns-topic", _options.BundleId);
        request.Headers.Add("apns-push-type", "alert");
        request.Headers.Add("apns-priority", "10");

        using var response = await _http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return PushResult.Sent;

        var reason = await ReasonAsync(response, ct);
        if (response.StatusCode == HttpStatusCode.Gone || reason is "BadDeviceToken" or "Unregistered") return PushResult.InvalidToken;
        throw new HttpRequestException($"APNs answered {(int)response.StatusCode} {reason ?? "(no reason)"}", null, response.StatusCode);
    }

    /// <summary>The <c>reason</c> of Apple's JSON error, or null when the body is not that (a proxy's error page, say).</summary>
    private static async Task<string?> ReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

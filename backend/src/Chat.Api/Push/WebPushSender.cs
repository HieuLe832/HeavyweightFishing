using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Chat.Data;
using Microsoft.AspNetCore.WebUtilities;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;

namespace Chat.Api.Push;

/// <summary>
/// Sends to browsers through the Web Push protocol (VAPID, encrypted payload). The stored token is the base64url of the browser's
/// subscription JSON, so it has no spaces. The notification text is never logged.
/// </summary>
public sealed class WebPushSender : IPushSender
{
    private readonly PushServiceClient _client;
    private readonly VapidAuthentication _vapid;

    public WebPushSender(PushServiceClient client, WebOptions options)
    {
        _client = client;
        _vapid = new VapidAuthentication(options.PublicKey!, options.PrivateKey!) { Subject = options.Subject };
    }

    public PushPlatform Platform => PushPlatform.Web;

    public async Task<PushResult> SendAsync(Chat.Api.Push.PushMessage message, string token, CancellationToken ct)
    {
        if (!TryDecode(token, out var subscription)) return PushResult.InvalidToken;
        try
        {
            await _client.RequestPushMessageDeliveryAsync(subscription!, new Lib.Net.Http.WebPush.PushMessage(Payload(message)), _vapid, VapidAuthenticationScheme.Vapid, ct);
            return PushResult.Sent;
        }
        catch (PushServiceClientException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return PushResult.InvalidToken;
        }
    }

    /// <summary>What the service worker receives: the text to show and the ids it needs to open the chat.</summary>
    public static string Payload(Chat.Api.Push.PushMessage message) => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["title"] = message.Title,
        ["body"] = message.Body,
        ["conversationId"] = message.ConversationId.ToString(),
        ["messageId"] = message.MessageId.ToString(),
        ["seq"] = message.Seq.ToString(),
    });

    /// <summary>The token a client stores for a browser subscription.</summary>
    public static string Encode(string endpoint, string p256dh, string auth) =>
        WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { endpoint, keys = new { p256dh, auth } }));

    /// <summary>
    /// Decodes a token. It must carry an <c>https</c> endpoint on a public address: the endpoint is a URL a client chose, and the API
    /// will POST to it, so loopback, private and link-local addresses are refused.
    /// </summary>
    public static bool TryDecode(string token, out PushSubscription? subscription)
    {
        subscription = null;
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(WebEncoders.Base64UrlDecode(token));
            if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("endpoint", out var endpoint) || endpoint.ValueKind != JsonValueKind.String) return false;
            if (!json.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Object) return false;
            var p256dh = Text(keys, "p256dh");
            var auth = Text(keys, "auth");
            if (string.IsNullOrEmpty(p256dh) || string.IsNullOrEmpty(auth) || !IsPublicHttps(endpoint.GetString())) return false;
            subscription = new PushSubscription { Endpoint = endpoint.GetString() };
            subscription.SetKey(PushEncryptionKeyName.P256DH, p256dh);
            subscription.SetKey(PushEncryptionKeyName.Auth, auth);
            return true;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The handler for Web Push calls. <see cref="TryDecode"/> only sees the endpoint's text, and a public-looking name can resolve to a
    /// private address, so the check is repeated where the socket connects: only a public address is ever dialled.
    /// </summary>
    public static SocketsHttpHandler PublicOnlyHandler() => new()
    {
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
            var target = addresses.FirstOrDefault(IsPublic)
                ?? throw new HttpRequestException($"The push endpoint host {host} does not resolve to a public address.");
            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsPublicHttps(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.IdnHost;
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)) return false;
        return !IPAddress.TryParse(host.Trim('[', ']'), out var ip) || IsPublic(ip);
    }

    private static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;
        var bytes = ip.GetAddressBytes();
        return ip.AddressFamily switch
        {
            // 0/8 "this network", 10/8, 100.64/10 carrier-grade NAT, 127/8, 169.254/16 link-local (cloud metadata), 172.16/12,
            // 192.168/16, and 224/3 multicast and reserved.
            AddressFamily.InterNetwork => !(bytes[0] is 0 or 10 or 127 or >= 224 || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254)),
            AddressFamily.InterNetworkV6 => !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (bytes[0] & 0xFE) == 0xFC),
            _ => false,
        };
    }
}

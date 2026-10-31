using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chat.Data;

namespace Chat.Api.Push;

/// <summary>Sends to Android (and any FCM) devices through the HTTP v1 API. The notification text is never logged.</summary>
public sealed class FcmPushSender(HttpClient http, ServiceAccount account, IAccessTokenProvider tokens) : IPushSender
{
    public PushPlatform Platform => PushPlatform.Fcm;

    public async Task<PushResult> SendAsync(PushMessage message, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{Uri.EscapeDataString(account.ProjectId)}/messages:send")
        {
            Content = JsonContent.Create(new
            {
                message = new
                {
                    token,
                    notification = new { title = message.Title, body = message.Body },
                    data = new Dictionary<string, string>
                    {
                        ["conversationId"] = message.ConversationId.ToString(),
                        ["messageId"] = message.MessageId.ToString(),
                        ["seq"] = message.Seq.ToString(),
                    },
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(ct));

        using var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return PushResult.Sent;

        var error = await ErrorAsync(response, ct);
        if (IsDeadToken(response.StatusCode, error)) return PushResult.InvalidToken;
        throw new HttpRequestException($"FCM answered {(int)response.StatusCode} {error?.Status ?? "(no status)"}", null, response.StatusCode);
    }

    /// <summary>
    /// 404 <c>UNREGISTERED</c> means the app was uninstalled. 400 <c>INVALID_ARGUMENT</c> is a dead token only when the message is about
    /// the registration token: the same status also reports a malformed payload, which is our bug and must not clear anyone's token.
    /// </summary>
    private static bool IsDeadToken(HttpStatusCode status, FcmError? error) => error is not null && status switch
    {
        HttpStatusCode.NotFound => error.ErrorCodes.Contains("UNREGISTERED"),
        HttpStatusCode.BadRequest => error.Status == "INVALID_ARGUMENT" && error.Message.Contains("registration token", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private sealed record FcmError(string? Status, string Message, IReadOnlyList<string> ErrorCodes);

    /// <summary>Google's JSON error, or null when the body is not that (a proxy's error page, say).</summary>
    private static async Task<FcmError?> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) return null;
            var codes = error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array
                ? details.EnumerateArray().Where(d => d.ValueKind == JsonValueKind.Object && d.TryGetProperty("errorCode", out _)).Select(d => d.GetProperty("errorCode").GetString() ?? "").ToList()
                : [];
            return new FcmError(
                error.TryGetProperty("status", out var status) ? status.GetString() : null,
                error.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "",
                codes);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

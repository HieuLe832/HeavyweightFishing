using System.Net;
using System.Text.Json;
using Chat.Api.Push;
using Chat.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Chat.Api.Tests;

public class FcmPushSenderTests : IDisposable
{
    private static readonly PushMessage Message = new("Nguyễn Văn A", "Chào bạn 🎣", Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"), 42);

    private readonly FakeHandler _handler = new();
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"fcm-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_file);

    private sealed class FixedToken(string value) : IAccessTokenProvider
    {
        public Task<string> GetAsync(CancellationToken ct) => Task.FromResult(value);
    }

    private FcmPushSender Sender() => new(new HttpClient(_handler), new ServiceAccount("proj-1", "bot@x", "PEM", "https://t.example/token"), new FixedToken("ya29.fake"));

    [Fact]
    public async Task The_request_posts_to_the_project_send_url_with_a_bearer_token()
    {
        var result = await Sender().SendAsync(Message, "device-token", CancellationToken.None);

        Assert.Equal(PushResult.Sent, result);
        var request = Assert.Single(_handler.Requests).Message;
        Assert.Equal("https://fcm.googleapis.com/v1/projects/proj-1/messages:send", request.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("ya29.fake", request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task The_body_has_token_notification_and_string_data_with_unicode_intact()
    {
        await Sender().SendAsync(Message, "device-token", CancellationToken.None);

        var message = JsonSerializer.Deserialize<JsonElement>(_handler.Requests.Single().Body).GetProperty("message");
        Assert.Equal("device-token", message.GetProperty("token").GetString());
        Assert.Equal("Nguyễn Văn A", message.GetProperty("notification").GetProperty("title").GetString());
        Assert.Equal("Chào bạn 🎣", message.GetProperty("notification").GetProperty("body").GetString());
        var data = message.GetProperty("data");
        Assert.Equal(Message.ConversationId.ToString(), data.GetProperty("conversationId").GetString());
        Assert.Equal(Message.MessageId.ToString(), data.GetProperty("messageId").GetString());
        Assert.Equal("42", data.GetProperty("seq").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "{}", PushResult.Sent)]
    [InlineData(HttpStatusCode.NotFound, """{"error":{"code":404,"status":"NOT_FOUND","message":"Requested entity was not found.","details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"UNREGISTERED"}]}}""", PushResult.InvalidToken)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"code":400,"status":"INVALID_ARGUMENT","message":"The registration token is not a valid FCM registration token"}}""", PushResult.InvalidToken)]
    public async Task A_dead_token_is_reported_and_success_is_sent(HttpStatusCode status, string body, PushResult expected)
    {
        _handler.Queue(status, body);

        Assert.Equal(expected, await Sender().SendAsync(Message, "device-token", CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"code":400,"status":"INVALID_ARGUMENT","message":"Invalid JSON payload received. Unknown name \"token\" at 'message'"}}""", "application/json")]
    [InlineData(HttpStatusCode.NotFound, """{"error":{"code":404,"status":"NOT_FOUND","message":"Project not found"}}""", "application/json")]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"code":401,"status":"UNAUTHENTICATED"}}""", "application/json")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":{"code":429,"status":"RESOURCE_EXHAUSTED"}}""", "application/json")]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"error":{"code":503,"status":"UNAVAILABLE"}}""", "application/json")]
    [InlineData(HttpStatusCode.BadGateway, "<html>Bad gateway</html>", "text/html")]
    public async Task Every_other_answer_throws_so_the_token_is_kept(HttpStatusCode status, string body, string contentType)
    {
        _handler.Queue(status, body, contentType);

        await Assert.ThrowsAsync<HttpRequestException>(() => Sender().SendAsync(Message, "device-token", CancellationToken.None));
    }

    [Fact]
    public void A_valid_fcm_section_registers_the_fcm_sender()
    {
        File.WriteAllText(_file, JsonSerializer.Serialize(new { project_id = "proj-1", client_email = "bot@x", private_key = "PEM" }));

        var senders = PushRegistrationTests.Build(new() { ["Push:Fcm:ServiceAccountJsonPath"] = _file }).GetServices<IPushSender>().ToList();

        Assert.IsType<FcmPushSender>(senders.Single(s => s.Platform == PushPlatform.Fcm));
        Assert.IsType<LoggingPushSender>(senders.Single(s => s.Platform == PushPlatform.Apns));
    }

    [Fact]
    public void A_service_account_file_with_missing_fields_stops_the_start()
    {
        File.WriteAllText(_file, """{"client_email":"bot@x"}""");

        var e = Assert.Throws<InvalidOperationException>(() => PushRegistrationTests.Build(new() { ["Push:Fcm:ServiceAccountJsonPath"] = _file }));

        Assert.Contains("project_id", e.Message);
    }
}

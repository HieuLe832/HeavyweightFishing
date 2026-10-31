using System.Net;
using System.Text;

namespace Chat.Api.Tests;

public sealed record RecordedRequest(HttpRequestMessage Message, string Body);

/// <summary>Stands in for a provider's server: records every request (with its body read) and answers from a queue, 200 when it is empty.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string? Body, string ContentType)> _responses = new();
    private readonly List<RecordedRequest> _requests = [];

    public IReadOnlyList<RecordedRequest> Requests => _requests;

    public FakeHandler Queue(HttpStatusCode status, string? body = null, string contentType = "application/json")
    {
        _responses.Enqueue((status, body, contentType));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        _requests.Add(new RecordedRequest(request, body));
        var (status, responseBody, contentType) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, null, "application/json");
        var response = new HttpResponseMessage(status) { RequestMessage = request };
        if (responseBody is not null) response.Content = new StringContent(responseBody, Encoding.UTF8, contentType);
        return response;
    }
}

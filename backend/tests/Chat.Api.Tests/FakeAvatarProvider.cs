using System.Net;
using System.Text;
using System.Text.Json;

namespace Chat.Api.Tests;

/// <summary>
/// Stands in for the Avatar Provider container: serves <c>catalog.json</c> and one file per key, and can be taken down,
/// slowed or made to answer garbage.
/// </summary>
public sealed class FakeAvatarProvider : HttpMessageHandler
{
    private readonly Dictionary<string, byte[]> _files = new();
    private (string Body, string ContentType)? _rawCatalog;

    public bool Down { get; set; }
    public TimeSpan Delay { get; set; }
    public int CatalogRequests { get; private set; }

    /// <summary>Adds (or replaces) an avatar served as <c>{key}.webp</c>.</summary>
    public void Set(string key, byte[] bytes) => _files[key] = bytes;

    /// <summary>Answers <c>catalog.json</c> with this body instead of the generated one.</summary>
    public void SetRawCatalog(string body, string contentType = "application/json") => _rawCatalog = (body, contentType);

    public void Clear()
    {
        _files.Clear();
        _rawCatalog = null;
        Down = false;
        Delay = TimeSpan.Zero;
        CatalogRequests = 0;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
        if (Down) throw new HttpRequestException("Connection refused (avatars.test)");

        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        if (path == "catalog.json")
        {
            CatalogRequests++;
            var (body, type) = _rawCatalog ?? (JsonSerializer.Serialize(new
            {
                avatars = _files.Keys.Order(StringComparer.Ordinal).Select(k => new { key = k, file = $"{k}.webp" }),
            }), "application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };
        }
        if (path.EndsWith(".webp", StringComparison.Ordinal) && _files.TryGetValue(path[..^5], out var bytes))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    // The client factory disposes handlers it rotates out; this one lives as long as the fixture.
    protected override void Dispose(bool disposing) { }
}

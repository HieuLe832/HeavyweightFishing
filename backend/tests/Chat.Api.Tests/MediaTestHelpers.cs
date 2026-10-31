using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

/// <summary>Tiny files that carry a real header but no pixels: all the API looks at.</summary>
public static class TestFiles
{
    private static byte[] BigEndian32(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    private static byte[] LittleEndian16(int v) => [(byte)v, (byte)(v >> 8)];

    public static byte[] Png(int width = 4, int height = 3) =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, .. "IHDR"u8.ToArray(), .. BigEndian32(width), .. BigEndian32(height), 8, 6, 0, 0, 0, 0, 0, 0, 0];

    public static byte[] Jpeg(int width = 4, int height = 3) =>
        [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, .. "JFIF\0"u8.ToArray(), 1, 1, 0, 0, 1, 0, 1, 0, 0,
         0xFF, 0xC0, 0, 17, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xD9];

    public static byte[] Gif(int width = 4, int height = 3) =>
        [.. "GIF89a"u8.ToArray(), .. LittleEndian16(width), .. LittleEndian16(height), 0, 0, 0, 0x3B];

    public static byte[] WebPExtended(int width = 4, int height = 3) =>
        [.. "RIFF"u8.ToArray(), 22, 0, 0, 0, .. "WEBP"u8.ToArray(), .. "VP8X"u8.ToArray(), 10, 0, 0, 0, 0, 0, 0, 0,
         (byte)(width - 1), (byte)((width - 1) >> 8), (byte)((width - 1) >> 16), (byte)(height - 1), (byte)((height - 1) >> 8), (byte)((height - 1) >> 16)];

    public static byte[] WebPLossy(int width = 4, int height = 3) =>
        [.. "RIFF"u8.ToArray(), 26, 0, 0, 0, .. "WEBP"u8.ToArray(), .. "VP8 "u8.ToArray(), 10, 0, 0, 0, 0, 0, 0, 0x9D, 0x01, 0x2A,
         (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8)];

    public static byte[] WebPLossless(int width = 4, int height = 3)
    {
        var w = width - 1;
        var h = height - 1;
        return [.. "RIFF"u8.ToArray(), 20, 0, 0, 0, .. "WEBP"u8.ToArray(), .. "VP8L"u8.ToArray(), 5, 0, 0, 0, 0x2F,
                (byte)w, (byte)(((w >> 8) & 0x3F) | ((h & 0x03) << 6)), (byte)((h >> 2) & 0xFF), (byte)((h >> 10) & 0x0F)];
    }

    public static byte[] Pdf() => "%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n"u8.ToArray();

    public static byte[] Text(string text = "just some notes\n") => Encoding.UTF8.GetBytes(text);

    public static byte[] Svg() => """<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>"""u8.ToArray();
}

public static class MediaTestHelpers
{
    public static Task<HttpResponseMessage> UploadAsync(
        this HttpClient client, Person who, byte[] bytes, string fileName = "file.bin", string contentType = "application/octet-stream", string partName = "file")
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        var form = new MultipartFormDataContent { { part, partName, fileName } };
        var request = Authed(HttpMethod.Post, "/media", who.Token);
        request.Content = form;
        return client.SendAsync(request);
    }

    /// <summary>Uploads a file that must be accepted and returns its id.</summary>
    public static async Task<Guid> UploadedAsync(this ApiFixture api, Person who, byte[]? bytes = null)
    {
        var response = await api.Client.UploadAsync(who, bytes ?? TestFiles.Png());
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    public static Task<HttpResponseMessage> DownloadAsync(this ApiFixture api, Person who, Guid mediaId) =>
        api.Client.GetAsync(who, $"/media/{mediaId}");

    public static async Task<JsonElement> SendMediaAsync(
        this Microsoft.AspNetCore.SignalR.Client.HubConnection hub, Guid conversationId, IEnumerable<Guid> mediaIds, string? caption = null, Guid? clientMsgId = null, Guid? replyToId = null) =>
        await Microsoft.AspNetCore.SignalR.Client.HubConnectionExtensions.InvokeAsync<JsonElement>(
            hub, "SendMedia", conversationId, clientMsgId ?? Guid.NewGuid(), caption, mediaIds.ToList(), replyToId);
}

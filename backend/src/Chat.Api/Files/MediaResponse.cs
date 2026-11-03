using Microsoft.AspNetCore.Mvc;

namespace Chat.Api.Files;

public static class MediaResponse
{
    /// <summary>A file's bytes with the locked-down headers every download gets (see docs/pitfalls.md, "Media").</summary>
    public static IActionResult Serve(HttpContext http, MediaDownload file, string cacheControl = "private, max-age=3600")
    {
        var headers = http.Response.Headers;
        headers.ETag = file.ETag;
        headers.CacheControl = cacheControl;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        headers.ContentDisposition = file.Inline ? "inline" : "attachment";
        if (http.Request.Headers.IfNoneMatch == file.ETag) return new StatusCodeResult(StatusCodes.Status304NotModified);
        return new FileContentResult(file.Content, file.MimeType == "text/plain" ? "text/plain; charset=utf-8" : file.MimeType);
    }
}

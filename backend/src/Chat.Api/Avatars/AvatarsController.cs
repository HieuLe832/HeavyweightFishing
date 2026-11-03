using Chat.Api.Files;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chat.Api.Avatars;

[Route("avatars"), Authorize]
public class AvatarsController(AvatarCatalog catalog) : ApiControllerBase
{
    /// <summary>Short, so a provider that goes away stops showing within minutes even in an open tab.</summary>
    public const string ImageCacheControl = "private, max-age=300";

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var keys = await catalog.KeysAsync(ct);
        return keys.Error is { } error ? Problem(error) : Ok(new { avatars = keys.Value!.Select(key => new { key }) });
    }

    [HttpGet("{key}")]
    public async Task<IActionResult> Get(string key, CancellationToken ct)
    {
        var image = await catalog.OpenAsync(key, ct);
        return image.Error is { } error ? Problem(error) : MediaResponse.Serve(HttpContext, image.Value!, ImageCacheControl);
    }
}

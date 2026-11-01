using Chat.Api.Auth;
using Chat.Api.Files;

namespace Chat.Api.Avatars;

public static class AvatarEndpoints
{
    /// <summary>Short, so a provider that goes away stops showing within minutes even in an open tab.</summary>
    public const string ImageCacheControl = "private, max-age=300";

    public static void MapAvatars(this IEndpointRouteBuilder app)
    {
        app.MapGet("/avatars", async (AvatarCatalog catalog, CancellationToken ct) =>
        {
            var keys = await catalog.KeysAsync(ct);
            return keys.Error is { } error
                ? ProblemResults.Problem(error)
                : Results.Ok(new { avatars = keys.Value!.Select(key => new { key }) });
        }).RequireAuthorization();

        app.MapGet("/avatars/{key}", async (string key, HttpContext http, AvatarCatalog catalog, CancellationToken ct) =>
        {
            var image = await catalog.OpenAsync(key, ct);
            return image.Error is { } error ? ProblemResults.Problem(error) : MediaEndpoints.Serve(http, image.Value!, ImageCacheControl);
        }).RequireAuthorization();
    }
}

using Chat.Api.Auth;
using Chat.Api.Conversations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chat.Api.Files;

/// <param name="PresetKey">A character from the Avatar Provider's pack (own avatar only).</param>
public record AvatarRequest(Guid? MediaId, string? PresetKey = null);

[Authorize]
public class MediaController(MediaService media) : ApiControllerBase
{
    /// <remarks>
    /// No parameters on purpose: MVC reads a form body to bind any parameter, and the rate limit must be checked before the file
    /// streams in, and a body over the limit turned into <c>file_too_large</c>.
    /// </remarks>
    [HttpPost("media"), UploadLimits]
    public async Task<IActionResult> Upload()
    {
        var ct = HttpContext.RequestAborted;
        // Before the body is read: a caller over the limit must not get to stream another file in.
        if (!media.TryStartUpload(UserId)) return Problem(AuthError.RateLimited);
        if (!Request.HasFormContentType) return Invalid(new() { ["file"] = ["Send multipart/form-data with a part named file."] });
        IFormCollection form;
        try
        {
            form = await Request.ReadFormAsync(ct);
        }
        catch (Exception e) when (e is InvalidDataException or BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
        {
            return Problem(AuthError.FileTooLarge);
        }
        if (form.Files.GetFile("file") is not { } file) return Invalid(new() { ["file"] = ["Send the file in a part named file."] });

        await using var stream = file.OpenReadStream();
        var result = await media.UploadAsync(UserId, stream, file.Length, ct);
        return result.Error is { } error ? Problem(error) : Created($"/media/{result.Value!.Id}", result.Value);
    }

    [HttpGet("media/{id:guid}")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var result = await media.OpenAsync(UserId, id, ct);
        return result.Error is { } error ? Problem(error) : MediaResponse.Serve(HttpContext, result.Value!);
    }

    [HttpPut("users/me/avatar")]
    public async Task<IActionResult> SetOwnAvatar(AvatarRequest request, CancellationToken ct)
    {
        if ((request.MediaId is null) == (request.PresetKey is null))
            return Invalid(new() { ["mediaId"] = ["Send the id of an uploaded image, or a presetKey, not both."] });
        return NoContentOr(await media.SetUserAvatarAsync(UserId, request.MediaId, request.PresetKey, ct));
    }

    [HttpDelete("users/me/avatar")]
    public async Task<IActionResult> RemoveOwnAvatar(CancellationToken ct) =>
        NoContentOr(await media.SetUserAvatarAsync(UserId, null, null, ct));

    [HttpPut("conversations/{id:guid}/avatar")]
    public async Task<IActionResult> SetGroupAvatar(
        Guid id, AvatarRequest request, [FromServices] GroupService groups, [FromServices] ConversationReader reader, CancellationToken ct)
    {
        if (request.MediaId is not { } mediaId) return Invalid(new() { ["mediaId"] = ["Send the id of an uploaded image."] });
        if (await media.CheckAvatarAsync(UserId, mediaId, ct) is { } error) return Problem(error);
        return await Changed(await groups.SetAvatarAsync(UserId, id, mediaId, ct), id, reader, ct);
    }

    [HttpDelete("conversations/{id:guid}/avatar")]
    public async Task<IActionResult> RemoveGroupAvatar(
        Guid id, [FromServices] GroupService groups, [FromServices] ConversationReader reader, CancellationToken ct) =>
        await Changed(await groups.SetAvatarAsync(UserId, id, null, ct), id, reader, ct);

    private async Task<IActionResult> Changed(AuthError? error, Guid id, ConversationReader reader, CancellationToken ct) =>
        error is not null ? Problem(error) : OkOr(await reader.DetailAsync(UserId, id, ct));
}

using System.Security.Claims;
using Chat.Api.Auth;
using Chat.Api.Conversations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Chat.Api.Files;

public record AvatarRequest(Guid? MediaId);

public static class MediaEndpoints
{
    /// <summary>Room for the multipart boundaries and part headers around the file.</summary>
    private const long MultipartAllowance = 64 * 1024;

    public static void MapMedia(this IEndpointRouteBuilder app)
    {
        // The API reads the whole file to check it, so the request limit is the file limit plus room for the multipart framing.
        var maxRequest = app.ServiceProvider.GetRequiredService<IOptions<MediaOptions>>().Value.MaxBytes + MultipartAllowance;
        app.MapPost("/media", async (HttpRequest request, ClaimsPrincipal me, MediaService svc, CancellationToken ct) =>
        {
            // Before the body is read: a caller over the limit must not get to stream another file in.
            if (!svc.TryStartUpload(me.UserId())) return ProblemResults.Problem(AuthError.RateLimited);
            if (!request.HasFormContentType) return ProblemResults.Invalid(new() { ["file"] = ["Send multipart/form-data with a part named file."] });
            IFormCollection form;
            try
            {
                form = await request.ReadFormAsync(ct);
            }
            catch (Exception e) when (e is InvalidDataException or BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
            {
                return ProblemResults.Problem(AuthError.FileTooLarge);
            }
            if (form.Files.GetFile("file") is not { } file) return ProblemResults.Invalid(new() { ["file"] = ["Send the file in a part named file."] });

            await using var stream = file.OpenReadStream();
            var result = await svc.UploadAsync(me.UserId(), stream, file.Length, ct);
            return result.Error is { } error ? ProblemResults.Problem(error) : Results.Created($"/media/{result.Value!.Id}", result.Value);
        }).RequireAuthorization().DisableAntiforgery()
          .WithMetadata(new RequestSizeLimitAttribute(maxRequest)).WithMetadata(new RequestFormLimitsAttribute { MultipartBodyLengthLimit = maxRequest });

        app.MapGet("/media/{id:guid}", async (Guid id, HttpContext http, ClaimsPrincipal me, MediaService svc, CancellationToken ct) =>
        {
            var result = await svc.OpenAsync(me.UserId(), id, ct);
            if (result.Error is { } error) return ProblemResults.Problem(error);

            var file = result.Value!;
            var headers = http.Response.Headers;
            headers.ETag = file.ETag;
            headers.CacheControl = "private, max-age=3600";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
            headers.ContentDisposition = file.Inline ? "inline" : "attachment";
            if (http.Request.Headers.IfNoneMatch == file.ETag) return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Bytes(file.Content, file.MimeType == "text/plain" ? "text/plain; charset=utf-8" : file.MimeType);
        }).RequireAuthorization();

        app.MapPut("/users/me/avatar", async (AvatarRequest request, ClaimsPrincipal me, MediaService svc, CancellationToken ct) =>
        {
            if (request.MediaId is not { } mediaId) return ProblemResults.Invalid(new() { ["mediaId"] = ["Send the id of an uploaded image."] });
            return NoContent(await svc.SetUserAvatarAsync(me.UserId(), mediaId, ct));
        }).RequireAuthorization();

        app.MapDelete("/users/me/avatar", async (ClaimsPrincipal me, MediaService svc, CancellationToken ct) =>
            NoContent(await svc.SetUserAvatarAsync(me.UserId(), null, ct))).RequireAuthorization();

        app.MapPut("/conversations/{id:guid}/avatar", async (
            Guid id, AvatarRequest request, ClaimsPrincipal me, MediaService media, GroupService groups, ConversationReader reader, CancellationToken ct) =>
        {
            if (request.MediaId is not { } mediaId) return ProblemResults.Invalid(new() { ["mediaId"] = ["Send the id of an uploaded image."] });
            if (await media.CheckAvatarAsync(me.UserId(), mediaId, ct) is { } error) return ProblemResults.Problem(error);
            return await Changed(await groups.SetAvatarAsync(me.UserId(), id, mediaId, ct), me, id, reader, ct);
        }).RequireAuthorization();

        app.MapDelete("/conversations/{id:guid}/avatar", async (
            Guid id, ClaimsPrincipal me, GroupService groups, ConversationReader reader, CancellationToken ct) =>
            await Changed(await groups.SetAvatarAsync(me.UserId(), id, null, ct), me, id, reader, ct)).RequireAuthorization();
    }

    private static IResult NoContent(AuthError? error) => error is null ? Results.NoContent() : ProblemResults.Problem(error);

    private static async Task<IResult> Changed(AuthError? error, ClaimsPrincipal me, Guid id, ConversationReader reader, CancellationToken ct)
    {
        if (error is not null) return ProblemResults.Problem(error);
        var detail = await reader.DetailAsync(me.UserId(), id, ct);
        return detail.Error is { } e ? ProblemResults.Problem(e) : Results.Ok(detail.Value);
    }
}

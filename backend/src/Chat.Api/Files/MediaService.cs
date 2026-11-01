using System.Security.Cryptography;
using Chat.Api.Auth;
using Chat.Api.Avatars;
using Chat.Api.Messaging;
using Chat.Api.Users;
using Chat.Data;
using Chat.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chat.Api.Files;

/// <summary>The 20-uploads-a-minute limit (see <see cref="MediaOptions"/>).</summary>
public class UploadRateLimiter(IOptions<MediaOptions> options, TimeProvider time)
    : SlidingWindowLimiter(() => (options.Value.UploadLimitCount, TimeSpan.FromSeconds(options.Value.UploadLimitWindowSeconds)), time);

public record MediaInfo(Guid Id, string MimeType, long SizeBytes, int? Width, int? Height);

/// <param name="Inline">Images only; everything else is offered as a download.</param>
public record MediaDownload(byte[] Content, string MimeType, string ETag, bool Inline);

/// <summary>Process 5.0: validates and stores uploads (D7), decides who may read a file, and sets avatars.</summary>
public class MediaService(
    ChatDbContext db, IObjectStore store, UserAccess access, IOptions<MediaOptions> options, UploadRateLimiter limiter,
    AvatarCatalog avatars, TimeProvider time, ILogger<MediaService> log)
{
    /// <summary>Takes one upload from the caller's allowance. The endpoint asks before it reads the request body.</summary>
    public bool TryStartUpload(Guid userId) => limiter.TryAcquire(userId);

    /// <summary>
    /// Checks the bytes (size, then type from the content), writes them to the object store and records the metadata.
    /// <paramref name="declaredLength"/> lets an oversize upload be refused without reading it. Call <see cref="TryStartUpload"/> first.
    /// </summary>
    public async Task<Result<MediaInfo>> UploadAsync(Guid userId, Stream content, long? declaredLength, CancellationToken ct)
    {
        var max = options.Value.MaxBytes;
        if (declaredLength > max) return AuthError.FileTooLarge;

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length > max) return AuthError.FileTooLarge;
        var bytes = buffer.ToArray();

        var sniffed = MediaSniffer.Detect(bytes);
        if (sniffed is null) return AuthError.UnsupportedMediaType;

        var now = time.GetUtcNow();
        var media = new Media
        {
            Id = Guid.NewGuid(), UploaderId = userId, StorageKey = $"media/{now:yyyy}/{now:MM}/{Guid.NewGuid():N}",
            MimeType = sniffed.MimeType, SizeBytes = bytes.Length, Width = sniffed.Width, Height = sniffed.Height,
            Sha256 = SHA256.HashData(bytes), Status = MediaStatus.Ready, CreatedAt = now,
        };

        await store.PutAsync(media.StorageKey, bytes, media.MimeType, ct);
        try
        {
            db.Media.Add(media);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await store.DeleteAsync(media.StorageKey, CancellationToken.None); // no row, so no orphan object
            throw;
        }
        return new MediaInfo(media.Id, media.MimeType, media.SizeBytes, media.Width, media.Height);
    }

    /// <summary>
    /// The file's bytes if the caller may read it, else <c>media_not_found</c> (the same answer for a file that does not exist,
    /// so ids cannot be probed). See the design spec for who may read what.
    /// </summary>
    public async Task<Result<MediaDownload>> OpenAsync(Guid userId, Guid mediaId, CancellationToken ct)
    {
        var media = await FindReadyAsync(mediaId, ct);
        if (media is null || !await CanReadAsync(userId, media, ct)) return AuthError.MediaNotFound;
        return await DownloadAsync(media, ct);
    }

    /// <summary>
    /// The file's bytes without the membership check, for a caller already allowed by other means: an admin reading a file that a
    /// report's evidence lists (<see cref="Moderation.ModerationService.CheckEvidenceFileAsync"/>, which audits the read).
    /// </summary>
    public async Task<Result<MediaDownload>> OpenAllowedAsync(Guid mediaId, CancellationToken ct) =>
        await FindReadyAsync(mediaId, ct) is { } media ? await DownloadAsync(media, ct) : AuthError.MediaNotFound;

    private Task<Media?> FindReadyAsync(Guid mediaId, CancellationToken ct) =>
        db.Media.AsNoTracking().SingleOrDefaultAsync(m => m.Id == mediaId && m.Status == MediaStatus.Ready, ct);

    private async Task<Result<MediaDownload>> DownloadAsync(Media media, CancellationToken ct)
    {
        var bytes = await store.GetAsync(media.StorageKey, ct);
        if (bytes is null)
        {
            log.LogError("Media {MediaId} has a row but no object at {Key}", media.Id, media.StorageKey);
            return AuthError.MediaNotFound;
        }
        return new MediaDownload(bytes, media.MimeType, $"\"{Convert.ToHexStringLower(media.Sha256)}\"", media.MimeType.StartsWith("image/", StringComparison.Ordinal));
    }

    private async Task<bool> CanReadAsync(Guid userId, Media media, CancellationToken ct)
    {
        if (media.UploaderId == userId) return true;

        var attached = await (
            from a in db.MessageAttachments
            join m in db.Messages on a.MessageId equals m.Id
            join member in db.ConversationMembers on m.ConversationId equals member.ConversationId
            where a.MediaId == media.Id && m.DeletedAt == null && member.UserId == userId && member.LeftAt == null
            select 1).AnyAsync(ct);
        if (attached) return true;

        var groupAvatar = await (
            from c in db.Conversations
            join member in db.ConversationMembers on c.Id equals member.ConversationId
            where c.AvatarMediaId == media.Id && member.UserId == userId && member.LeftAt == null
            select 1).AnyAsync(ct);
        if (groupAvatar) return true;

        // Someone's avatar is for people who may see that person (a user who blocked the viewer looks missing).
        var owners = await db.Users.Where(u => u.AvatarMediaId == media.Id).Select(u => u.Id).ToListAsync(ct);
        foreach (var owner in owners)
            if ((await access.FindVisibleAsync(userId, owner, ct)).Error is null) return true;
        return false;
    }

    /// <summary>An avatar must be a ready image the caller uploaded. Not theirs (or missing) is <c>media_not_found</c>.</summary>
    public async Task<AuthError?> CheckAvatarAsync(Guid userId, Guid mediaId, CancellationToken ct)
    {
        var media = await db.Media.AsNoTracking().SingleOrDefaultAsync(m => m.Id == mediaId && m.UploaderId == userId && m.Status == MediaStatus.Ready, ct);
        if (media is null) return AuthError.MediaNotFound;
        return media.MimeType.StartsWith("image/", StringComparison.Ordinal) ? null : AuthError.UnsupportedMediaType;
    }

    /// <summary>
    /// Sets the caller's avatar to an upload or to a preset key from the Avatar Provider (checked against its catalog), and
    /// clears the other; both null clears it. The two columns exclude each other (<c>ck_users_one_avatar</c>).
    /// </summary>
    public async Task<AuthError?> SetUserAvatarAsync(Guid userId, Guid? mediaId, string? presetKey, CancellationToken ct)
    {
        if (mediaId is { } id && await CheckAvatarAsync(userId, id, ct) is { } error) return error;
        if (presetKey is not null && await avatars.CheckKeyAsync(presetKey, ct) is { } presetError) return presetError;
        var now = time.GetUtcNow();
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.AvatarMediaId, mediaId).SetProperty(x => x.AvatarPresetKey, presetKey).SetProperty(x => x.UpdatedAt, now), ct);
        return null;
    }
}

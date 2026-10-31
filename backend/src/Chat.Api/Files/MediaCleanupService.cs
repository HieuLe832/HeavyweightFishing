using Chat.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chat.Api.Files;

/// <summary>
/// Removes files nothing uses any more: not an avatar, not on a live message, not on a reported message, and older than the
/// grace period. The rows are claimed and deleted in one transaction and the bytes after it, so a crash in between leaks bytes
/// (harmless) instead of leaving a row that points at nothing. <c>SKIP LOCKED</c> lets several API nodes run this together.
/// </summary>
public sealed class MediaCleanupService(
    IServiceScopeFactory scopes, IObjectStore store, IOptions<MediaCleanupOptions> options, TimeProvider time, ILogger<MediaCleanupService> log)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

    /// <summary>Runs batches until one comes back short. Returns how many media rows were removed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var settings = options.Value;
        var cutoff = time.GetUtcNow().AddDays(-settings.GraceDays);
        var total = 0;
        while (true)
        {
            var keys = await RemoveBatchAsync(cutoff, settings.BatchSize, ct);
            foreach (var key in keys)
            {
                try
                {
                    await store.DeleteAsync(key, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogWarning(e, "Could not delete object {StorageKey}; its media row is already gone, so the bytes are leaked", key);
                }
            }
            total += keys.Count;
            if (keys.Count < settings.BatchSize) return total;
        }
    }

    /// <summary>Deletes up to <paramref name="batch"/> unused rows and returns their storage keys.</summary>
    private async Task<List<string>> RemoveBatchAsync(DateTimeOffset cutoff, int batch, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT m.id AS "Value" FROM media m
            WHERE m.created_at < {cutoff}
              AND NOT EXISTS (SELECT 1 FROM users u WHERE u.avatar_media_id = m.id)
              AND NOT EXISTS (SELECT 1 FROM conversations c WHERE c.avatar_media_id = m.id)
              AND NOT EXISTS (
                  SELECT 1 FROM message_attachments a JOIN messages g ON g.id = a.message_id
                  WHERE a.media_id = m.id AND (g.deleted_at IS NULL OR EXISTS (SELECT 1 FROM reports r WHERE r.message_id = g.id)))
            ORDER BY m.created_at
            LIMIT {batch}
            FOR UPDATE OF m SKIP LOCKED
            """).ToListAsync(ct);
        if (ids.Count == 0) return [];

        var keys = await db.Media.Where(m => ids.Contains(m.Id)).Select(m => m.StorageKey).ToListAsync(ct);
        await db.MessageAttachments.Where(a => ids.Contains(a.MediaId)).ExecuteDeleteAsync(ct);
        await db.Media.Where(m => ids.Contains(m.Id)).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return keys;
    }
}

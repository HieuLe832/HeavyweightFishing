namespace Chat.Api.Realtime;

/// <summary>
/// Process 4.5's view of D9. Presence is a convenience, so a store failure is logged and answered with the safe
/// default instead of failing the connect, send or receipt that asked.
/// </summary>
public class PresenceService(IPresenceStore store, ConnectionRegistry registry, ILogger<PresenceService> log)
{
    /// <summary>True when this was the user's first live connection.</summary>
    public async Task<bool> ConnectedAsync(Guid userId, string connectionId, CancellationToken ct = default)
    {
        registry.Add(userId, connectionId);
        return await Safe(() => store.AddConnectionAsync(userId, connectionId, ct), false);
    }

    /// <summary>True when that was the user's last live connection.</summary>
    public async Task<bool> DisconnectedAsync(Guid userId, string connectionId)
    {
        registry.Remove(connectionId);
        return await Safe(() => store.RemoveConnectionAsync(userId, connectionId), false);
    }

    public Task RenewAsync(CancellationToken ct) =>
        Safe(async () => { await store.RenewAsync(registry.Snapshot(), ct); return true; }, false);

    /// <summary>Who is online. When the store is down this says nobody, so offline members get a push rather than silence.</summary>
    public Task<HashSet<Guid>> OnlineAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        Safe(() => store.OnlineAsync(userIds, ct), new HashSet<Guid>());

    public Task<HashSet<Guid>> TypingAsync(Guid conversationId, IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        Safe(() => store.TypingAsync(conversationId, userIds, ct), new HashSet<Guid>());

    public Task SetTypingAsync(Guid conversationId, Guid userId, CancellationToken ct = default) =>
        Safe(async () => { await store.SetTypingAsync(conversationId, userId, ct); return true; }, false);

    public Task<bool> ClearTypingAsync(Guid conversationId, Guid userId, CancellationToken ct = default) =>
        Safe(() => store.ClearTypingAsync(conversationId, userId, ct), false);

    private async Task<T> Safe<T>(Func<Task<T>> action, T fallback)
    {
        try
        {
            return await action();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "The presence store failed");
            return fallback;
        }
    }
}

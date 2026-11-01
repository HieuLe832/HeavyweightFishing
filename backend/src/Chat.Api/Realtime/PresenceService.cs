namespace Chat.Api.Realtime;

/// <summary>
/// Process 4.5's view of D9. Presence is a convenience, so a store failure is logged and answered with the safe
/// default instead of failing the connect, send or receipt that asked.
/// </summary>
public class PresenceService(IPresenceStore store, ConnectionRegistry registry, ILogger<PresenceService> log)
{
    /// <summary>
    /// The lease renewal in flight. A renewal writes the leases of the connections it listed at its start, so a connection that closes
    /// meanwhile would get its lease back and look online for another lease. A disconnect therefore waits for it before removing the
    /// lease. Disconnects do not wait for each other, and a store that lost its data is still refilled by the next renewal.
    /// </summary>
    private Task _renewal = Task.CompletedTask;

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
        await Volatile.Read(ref _renewal); // never faults: see RenewAsync
        return await Safe(() => store.RemoveConnectionAsync(userId, connectionId), false);
    }

    /// <summary>Renews the leases of this node's connections. Called by <see cref="PresenceRefresher"/> only, one at a time.</summary>
    public async Task RenewAsync(CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Interlocked.Exchange(ref _renewal, done.Task); // published before the list is taken, so a later disconnect sees it
        try
        {
            await Safe(async () => { await store.RenewAsync(registry.Snapshot(), ct); return true; }, false);
        }
        finally
        {
            done.SetResult();
        }
    }

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

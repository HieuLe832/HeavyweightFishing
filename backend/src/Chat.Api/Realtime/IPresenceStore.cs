namespace Chat.Api.Realtime;

/// <summary>
/// D9 Presence Cache. A user is online while at least one of their connections holds a live lease; typing is a key that
/// expires on its own. Nothing here is durable, and nothing here may be the only copy of a fact.
/// </summary>
public interface IPresenceStore
{
    /// <summary>Registers a connection. True when the user had no live connection before (the offline-to-online edge).</summary>
    Task<bool> AddConnectionAsync(Guid userId, string connectionId, CancellationToken ct = default);

    /// <summary>Drops a connection. True when it was the user's last live one (the online-to-offline edge).</summary>
    Task<bool> RemoveConnectionAsync(Guid userId, string connectionId, CancellationToken ct = default);

    /// <summary>Extends the leases of connections that are still open.</summary>
    Task RenewAsync(IReadOnlyCollection<(Guid UserId, string ConnectionId)> connections, CancellationToken ct = default);

    /// <summary>The users, out of <paramref name="userIds"/>, that have a live connection.</summary>
    Task<HashSet<Guid>> OnlineAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>Marks the user as typing; repeating it renews the signal.</summary>
    Task SetTypingAsync(Guid conversationId, Guid userId, CancellationToken ct = default);

    /// <summary>Clears the signal. True when it was set.</summary>
    Task<bool> ClearTypingAsync(Guid conversationId, Guid userId, CancellationToken ct = default);

    /// <summary>The users, out of <paramref name="userIds"/>, currently typing in the conversation.</summary>
    Task<HashSet<Guid>> TypingAsync(Guid conversationId, IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
}

using Chat.Api.Realtime;

namespace Chat.Api.Tests;

/// <summary>Wraps the in-memory presence store so a test can make every call fail, like a Redis outage.</summary>
public sealed class FaultablePresenceStore(IPresenceStore inner) : IPresenceStore
{
    public bool Fail { get; set; }

    private T Guard<T>(Func<T> call) => Fail ? throw new InvalidOperationException("presence store is down") : call();

    public Task<bool> AddConnectionAsync(Guid userId, string connectionId, CancellationToken ct = default) =>
        Guard(() => inner.AddConnectionAsync(userId, connectionId, ct));

    public Task<bool> RemoveConnectionAsync(Guid userId, string connectionId, CancellationToken ct = default) =>
        Guard(() => inner.RemoveConnectionAsync(userId, connectionId, ct));

    public Task RenewAsync(IReadOnlyCollection<(Guid UserId, string ConnectionId)> connections, CancellationToken ct = default) =>
        Guard(() => inner.RenewAsync(connections, ct));

    public Task<HashSet<Guid>> OnlineAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        Guard(() => inner.OnlineAsync(userIds, ct));

    public Task SetTypingAsync(Guid conversationId, Guid userId, CancellationToken ct = default) =>
        Guard(() => inner.SetTypingAsync(conversationId, userId, ct));

    public Task<bool> ClearTypingAsync(Guid conversationId, Guid userId, CancellationToken ct = default) =>
        Guard(() => inner.ClearTypingAsync(conversationId, userId, ct));

    public Task<HashSet<Guid>> TypingAsync(Guid conversationId, IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        Guard(() => inner.TypingAsync(conversationId, userIds, ct));
}

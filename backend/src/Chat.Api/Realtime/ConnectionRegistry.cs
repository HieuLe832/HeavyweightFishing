using System.Collections.Concurrent;

namespace Chat.Api.Realtime;

/// <summary>The hub connections held by this node, so <see cref="PresenceRefresher"/> can renew their leases.</summary>
public class ConnectionRegistry
{
    private readonly ConcurrentDictionary<string, Guid> _connections = new();

    public void Add(Guid userId, string connectionId) => _connections[connectionId] = userId;

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    public IReadOnlyCollection<(Guid UserId, string ConnectionId)> Snapshot() =>
        _connections.Select(c => (c.Value, c.Key)).ToList();
}

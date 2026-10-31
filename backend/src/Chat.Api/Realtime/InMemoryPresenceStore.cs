using Microsoft.Extensions.Options;

namespace Chat.Api.Realtime;

/// <summary>One-node D9, used when Redis is not configured and in tests. Expiry follows the <see cref="TimeProvider"/>.</summary>
public class InMemoryPresenceStore(IOptions<RealtimeOptions> options, TimeProvider time) : IPresenceStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Dictionary<string, DateTimeOffset>> _leases = new();
    private readonly Dictionary<(Guid Conversation, Guid User), DateTimeOffset> _typing = new();

    private DateTimeOffset LeaseEnd => time.GetUtcNow().AddSeconds(options.Value.LeaseSeconds);

    public Task<bool> AddConnectionAsync(Guid userId, string connectionId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var leases = Live(userId);
            var wasOffline = leases.Count == 0;
            leases[connectionId] = LeaseEnd;
            return Task.FromResult(wasOffline);
        }
    }

    public Task<bool> RemoveConnectionAsync(Guid userId, string connectionId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var leases = Live(userId);
            return Task.FromResult(leases.Remove(connectionId) && leases.Count == 0);
        }
    }

    public Task RenewAsync(IReadOnlyCollection<(Guid UserId, string ConnectionId)> connections, CancellationToken ct = default)
    {
        lock (_gate)
            foreach (var (userId, connectionId) in connections) Live(userId)[connectionId] = LeaseEnd;
        return Task.CompletedTask;
    }

    public Task<HashSet<Guid>> OnlineAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(userIds.Where(id => Live(id).Count > 0).ToHashSet());
    }

    public Task SetTypingAsync(Guid conversationId, Guid userId, CancellationToken ct = default)
    {
        lock (_gate) _typing[(conversationId, userId)] = time.GetUtcNow().AddSeconds(options.Value.TypingSeconds);
        return Task.CompletedTask;
    }

    public Task<bool> ClearTypingAsync(Guid conversationId, Guid userId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var live = _typing.TryGetValue((conversationId, userId), out var until) && until > time.GetUtcNow();
            _typing.Remove((conversationId, userId));
            return Task.FromResult(live);
        }
    }

    public Task<HashSet<Guid>> TypingAsync(Guid conversationId, IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            return Task.FromResult(userIds.Where(id => _typing.TryGetValue((conversationId, id), out var until) && until > now).ToHashSet());
        }
    }

    /// <summary>The user's leases with the expired ones dropped. Call under the lock.</summary>
    private Dictionary<string, DateTimeOffset> Live(Guid userId)
    {
        if (!_leases.TryGetValue(userId, out var leases)) _leases[userId] = leases = new();
        var now = time.GetUtcNow();
        foreach (var expired in leases.Where(l => l.Value <= now).Select(l => l.Key).ToList()) leases.Remove(expired);
        return leases;
    }
}

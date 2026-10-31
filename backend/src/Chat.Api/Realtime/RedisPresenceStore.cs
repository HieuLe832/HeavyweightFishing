using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Chat.Api.Realtime;

/// <summary>
/// D9 in Redis. <c>presence:{user}</c> is a sorted set of connection ids scored by lease expiry (unix ms);
/// <c>typing:{conversation}:{user}</c> is a key with a TTL. The scripts run atomically, so of several simultaneous
/// connects exactly one sees the offline-to-online edge.
/// </summary>
public class RedisPresenceStore(IConnectionMultiplexer redis, IOptions<RealtimeOptions> options, TimeProvider time) : IPresenceStore
{
    // KEYS[1] set; ARGV: now, lease end, connection id, key TTL ms. Returns 1 when the set was empty (the user was offline).
    private const string AddScript = """
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[1])
        local before = redis.call('ZCARD', KEYS[1])
        redis.call('ZADD', KEYS[1], ARGV[2], ARGV[3])
        redis.call('PEXPIRE', KEYS[1], ARGV[4])
        if before == 0 then return 1 else return 0 end
        """;

    // KEYS[1] set; ARGV: now, connection id. Returns 1 when this removed the last live connection.
    private const string RemoveScript = """
        local removed = redis.call('ZREM', KEYS[1], ARGV[2])
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[1])
        if removed > 0 and redis.call('ZCARD', KEYS[1]) == 0 then return 1 else return 0 end
        """;

    private IDatabase Db => redis.GetDatabase();
    private static RedisKey PresenceKey(Guid userId) => $"presence:{userId:D}";
    private static RedisKey TypingKey(Guid conversationId, Guid userId) => $"typing:{conversationId:D}:{userId:D}";
    private long NowMs => time.GetUtcNow().ToUnixTimeMilliseconds();
    private long LeaseMs => options.Value.LeaseSeconds * 1000L;

    public async Task<bool> AddConnectionAsync(Guid userId, string connectionId, CancellationToken ct = default) =>
        (long)await Db.ScriptEvaluateAsync(AddScript, [PresenceKey(userId)],
            [NowMs, NowMs + LeaseMs, connectionId, LeaseMs * 2]) == 1;

    public async Task<bool> RemoveConnectionAsync(Guid userId, string connectionId, CancellationToken ct = default) =>
        (long)await Db.ScriptEvaluateAsync(RemoveScript, [PresenceKey(userId)], [NowMs, connectionId]) == 1;

    public async Task RenewAsync(IReadOnlyCollection<(Guid UserId, string ConnectionId)> connections, CancellationToken ct = default)
    {
        var db = Db;
        var end = NowMs + LeaseMs;
        var tasks = new List<Task>();
        foreach (var (userId, connectionId) in connections)
        {
            tasks.Add(db.SortedSetAddAsync(PresenceKey(userId), connectionId, end));
            tasks.Add(db.KeyExpireAsync(PresenceKey(userId), TimeSpan.FromMilliseconds(LeaseMs * 2)));
        }
        await Task.WhenAll(tasks);
    }

    public async Task<HashSet<Guid>> OnlineAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        var db = Db;
        var now = NowMs;
        var counts = userIds.Select(id => (id, count: db.SortedSetLengthAsync(PresenceKey(id), now, double.PositiveInfinity))).ToList();
        await Task.WhenAll(counts.Select(c => c.count));
        return counts.Where(c => c.count.Result > 0).Select(c => c.id).ToHashSet();
    }

    public Task SetTypingAsync(Guid conversationId, Guid userId, CancellationToken ct = default) =>
        Db.StringSetAsync(TypingKey(conversationId, userId), "1", TimeSpan.FromSeconds(options.Value.TypingSeconds));

    public Task<bool> ClearTypingAsync(Guid conversationId, Guid userId, CancellationToken ct = default) =>
        Db.KeyDeleteAsync(TypingKey(conversationId, userId));

    public async Task<HashSet<Guid>> TypingAsync(Guid conversationId, IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        var db = Db;
        var checks = userIds.Select(id => (id, exists: db.KeyExistsAsync(TypingKey(conversationId, id)))).ToList();
        await Task.WhenAll(checks.Select(c => c.exists));
        return checks.Where(c => c.exists.Result).Select(c => c.id).ToHashSet();
    }
}

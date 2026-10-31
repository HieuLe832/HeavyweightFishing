using Chat.Api.Realtime;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;

namespace Chat.Api.Tests;

public class RedisPresenceStoreTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private ConnectionMultiplexer _redis = null!;
    private RedisPresenceStore _store = null!;

    public async Task InitializeAsync()
    {
        _redis = await ConnectionMultiplexer.ConnectAsync(await SharedRedis.ConnectionStringAsync());
        _store = new RedisPresenceStore(_redis, Options.Create(new RealtimeOptions { TypingSeconds = 1 }), _time);
    }

    public async Task DisposeAsync() => await _redis.DisposeAsync();

    [Fact]
    public async Task Only_the_first_connection_and_the_last_disconnect_are_edges()
    {
        var user = Guid.NewGuid();

        Assert.True(await _store.AddConnectionAsync(user, "a"));
        Assert.False(await _store.AddConnectionAsync(user, "b"));
        Assert.Equal([user], await _store.OnlineAsync([user, Guid.NewGuid()]));
        Assert.False(await _store.RemoveConnectionAsync(user, "a"));
        Assert.False(await _store.RemoveConnectionAsync(user, "unknown"));
        Assert.True(await _store.RemoveConnectionAsync(user, "b"));
        Assert.Empty(await _store.OnlineAsync([user]));
        Assert.False(await _store.RemoveConnectionAsync(user, "b")); // already offline: no second edge
    }

    [Fact]
    public async Task A_lease_that_is_not_renewed_expires_and_renewing_keeps_it_alive()
    {
        var gone = Guid.NewGuid();
        var kept = Guid.NewGuid();
        await _store.AddConnectionAsync(gone, "c1");
        await _store.AddConnectionAsync(kept, "c2");

        _time.Advance(TimeSpan.FromSeconds(50));
        await _store.RenewAsync([(kept, "c2")]);
        _time.Advance(TimeSpan.FromSeconds(50));

        Assert.Equal([kept], await _store.OnlineAsync([gone, kept]));
        Assert.True(await _store.AddConnectionAsync(gone, "c3")); // a crashed node's user is a fresh offline-to-online edge
    }

    [Fact]
    public async Task Of_many_simultaneous_connects_exactly_one_sees_the_edge()
    {
        var user = Guid.NewGuid();

        var edges = await Task.WhenAll(Enumerable.Range(0, 25).Select(i => _store.AddConnectionAsync(user, $"c{i}")));

        Assert.Single(edges, e => e);
    }

    [Fact]
    public async Task Typing_is_set_cleared_and_lapses()
    {
        var conversation = Guid.NewGuid();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        await _store.SetTypingAsync(conversation, alice);
        await _store.SetTypingAsync(conversation, bob);
        Assert.Equal(new[] { alice, bob }.Order(), (await _store.TypingAsync(conversation, [alice, bob])).Order());
        Assert.True(await _store.ClearTypingAsync(conversation, alice));
        Assert.False(await _store.ClearTypingAsync(conversation, alice));
        Assert.Equal([bob], await _store.TypingAsync(conversation, [alice, bob]));

        await Task.Delay(1300); // Redis expires it itself (TypingSeconds = 1)

        Assert.Empty(await _store.TypingAsync(conversation, [alice, bob]));
    }
}

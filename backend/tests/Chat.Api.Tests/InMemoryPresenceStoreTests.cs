using Chat.Api.Realtime;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Chat.Api.Tests;

public class InMemoryPresenceStoreTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly InMemoryPresenceStore _store;

    public InMemoryPresenceStoreTests() => _store = new(Options.Create(new RealtimeOptions()), _time);

    [Fact]
    public async Task Edges_follow_the_first_connection_and_the_last_disconnect()
    {
        var user = Guid.NewGuid();

        Assert.True(await _store.AddConnectionAsync(user, "a"));
        Assert.False(await _store.AddConnectionAsync(user, "b"));
        Assert.False(await _store.RemoveConnectionAsync(user, "a"));
        Assert.True(await _store.RemoveConnectionAsync(user, "b"));
        Assert.False(await _store.RemoveConnectionAsync(user, "b"));
    }

    [Fact]
    public async Task Leases_expire_unless_renewed_and_typing_lapses()
    {
        var gone = Guid.NewGuid();
        var kept = Guid.NewGuid();
        var chat = Guid.NewGuid();
        await _store.AddConnectionAsync(gone, "c1");
        await _store.AddConnectionAsync(kept, "c2");
        await _store.SetTypingAsync(chat, kept);

        _time.Advance(TimeSpan.FromSeconds(50));
        await _store.RenewAsync([(kept, "c2")]);
        _time.Advance(TimeSpan.FromSeconds(50));

        Assert.Equal([kept], await _store.OnlineAsync([gone, kept]));
        Assert.Empty(await _store.TypingAsync(chat, [kept]));
    }
}

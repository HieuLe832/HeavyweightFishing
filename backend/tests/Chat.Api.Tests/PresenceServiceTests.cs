using Chat.Api.Realtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Chat.Api.Tests;

public class PresenceServiceTests
{
    /// <summary>A store whose renewal takes its list of connections, then waits for a release before writing the leases.</summary>
    private sealed class SlowRenewStore(IPresenceStore inner) : IPresenceStore
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RenewAsync(IReadOnlyCollection<(Guid UserId, string ConnectionId)> connections, CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Release.Task;
            await inner.RenewAsync(connections, ct);
        }

        public Task<bool> AddConnectionAsync(Guid userId, string connectionId, CancellationToken ct = default) => inner.AddConnectionAsync(userId, connectionId, ct);
        public Task<bool> RemoveConnectionAsync(Guid userId, string connectionId, CancellationToken ct = default) => inner.RemoveConnectionAsync(userId, connectionId, ct);
        public Task<HashSet<Guid>> OnlineAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) => inner.OnlineAsync(userIds, ct);
        public Task SetTypingAsync(Guid conversationId, Guid userId, CancellationToken ct = default) => inner.SetTypingAsync(conversationId, userId, ct);
        public Task<bool> ClearTypingAsync(Guid conversationId, Guid userId, CancellationToken ct = default) => inner.ClearTypingAsync(conversationId, userId, ct);
        public Task<HashSet<Guid>> TypingAsync(Guid conversationId, IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) => inner.TypingAsync(conversationId, userIds, ct);
    }

    [Fact]
    public async Task A_renewal_in_flight_cannot_put_back_the_lease_of_a_connection_that_just_closed()
    {
        var store = new SlowRenewStore(new InMemoryPresenceStore(Options.Create(new RealtimeOptions()), new FakeTimeProvider(DateTimeOffset.UtcNow)));
        var presence = new PresenceService(store, new ConnectionRegistry(), NullLogger<PresenceService>.Instance);
        var user = Guid.NewGuid();
        Assert.True(await presence.ConnectedAsync(user, "c1"));

        var renewal = presence.RenewAsync(CancellationToken.None); // takes its list while c1 is still open
        await store.Started.Task;
        var disconnect = presence.DisconnectedAsync(user, "c1");
        store.Release.SetResult();
        await renewal;

        Assert.True(await disconnect); // the last connection: the offline edge is still reported
        Assert.Empty(await presence.OnlineAsync([user]));
    }
}

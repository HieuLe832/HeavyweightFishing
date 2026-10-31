using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Chat.Api.Messaging;

/// <summary>A sliding-window limit on sends per user, kept in memory. Slice 2 moves it to Redis when there is more than one node.</summary>
public class MessageRateLimiter(IOptions<MessagingOptions> options, TimeProvider time)
{
    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _sends = new();

    public bool TryAcquire(Guid userId)
    {
        var o = options.Value;
        var now = time.GetUtcNow();
        var window = TimeSpan.FromSeconds(o.RateLimitWindowSeconds);
        var queue = _sends.GetOrAdd(userId, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= window) queue.Dequeue();
            if (queue.Count >= o.RateLimitCount) return false;
            queue.Enqueue(now);
            return true;
        }
    }
}

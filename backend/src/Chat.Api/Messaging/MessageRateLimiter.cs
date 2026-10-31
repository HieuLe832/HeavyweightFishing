using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Chat.Api.Messaging;

/// <summary>At most <c>count</c> acquisitions per user in a sliding window, kept in memory (per node).</summary>
public class SlidingWindowLimiter(Func<(int Count, TimeSpan Window)> limit, TimeProvider time)
{
    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _hits = new();

    public bool TryAcquire(Guid userId)
    {
        var (count, window) = limit();
        var now = time.GetUtcNow();
        var queue = _hits.GetOrAdd(userId, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= window) queue.Dequeue();
            if (queue.Count >= count) return false;
            queue.Enqueue(now);
            return true;
        }
    }
}

/// <summary>The send limit (see <see cref="MessagingOptions"/>).</summary>
public class MessageRateLimiter(IOptions<MessagingOptions> options, TimeProvider time)
    : SlidingWindowLimiter(() => (options.Value.RateLimitCount, TimeSpan.FromSeconds(options.Value.RateLimitWindowSeconds)), time);

/// <summary>The typing-signal limit. Going over it drops the signal quietly instead of failing the call.</summary>
public class TypingRateLimiter(IOptions<MessagingOptions> options, TimeProvider time)
    : SlidingWindowLimiter(() => (options.Value.TypingLimitCount, TimeSpan.FromSeconds(options.Value.TypingLimitWindowSeconds)), time);

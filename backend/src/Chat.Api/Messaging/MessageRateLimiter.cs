using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Chat.Api.Messaging;

/// <summary>
/// At most <c>count</c> acquisitions per user in a sliding window, kept in memory (per node). Every <see cref="SweepEvery"/>
/// acquisitions it forgets the users with no hit left in the window, so memory follows the active users, not every user ever seen.
/// </summary>
public class SlidingWindowLimiter(Func<(int Count, TimeSpan Window)> limit, TimeProvider time)
{
    public const int SweepEvery = 1024;

    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _hits = new();
    private int _acquisitions;

    /// <summary>How many users the limiter holds hits for (for tests and diagnostics).</summary>
    public int TrackedUsers => _hits.Count;

    public bool TryAcquire(Guid userId)
    {
        var (count, window) = limit();
        var now = time.GetUtcNow();
        while (true)
        {
            var queue = _hits.GetOrAdd(userId, _ => new Queue<DateTimeOffset>());
            lock (queue)
            {
                // The sweep removed this queue while we waited for its lock: take the new one, or the hit would be lost.
                if (!_hits.TryGetValue(userId, out var current) || current != queue) continue;
                Expire(queue, now, window);
                if (queue.Count >= count) return false;
                queue.Enqueue(now);
            }
            if (Interlocked.Increment(ref _acquisitions) % SweepEvery == 0) Sweep(now, window);
            return true;
        }
    }

    private void Sweep(DateTimeOffset now, TimeSpan window)
    {
        foreach (var (userId, queue) in _hits)
        {
            lock (queue)
            {
                Expire(queue, now, window);
                if (queue.Count == 0) _hits.TryRemove(new KeyValuePair<Guid, Queue<DateTimeOffset>>(userId, queue));
            }
        }
    }

    private static void Expire(Queue<DateTimeOffset> queue, DateTimeOffset now, TimeSpan window)
    {
        while (queue.Count > 0 && now - queue.Peek() >= window) queue.Dequeue();
    }
}

/// <summary>The send limit (see <see cref="MessagingOptions"/>).</summary>
public class MessageRateLimiter(IOptions<MessagingOptions> options, TimeProvider time)
    : SlidingWindowLimiter(() => (options.Value.RateLimitCount, TimeSpan.FromSeconds(options.Value.RateLimitWindowSeconds)), time);

/// <summary>The typing-signal limit. Going over it drops the signal quietly instead of failing the call.</summary>
public class TypingRateLimiter(IOptions<MessagingOptions> options, TimeProvider time)
    : SlidingWindowLimiter(() => (options.Value.TypingLimitCount, TimeSpan.FromSeconds(options.Value.TypingLimitWindowSeconds)), time);

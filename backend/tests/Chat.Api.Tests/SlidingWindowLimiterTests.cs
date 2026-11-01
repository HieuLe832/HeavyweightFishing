using Chat.Api.Messaging;
using Microsoft.Extensions.Time.Testing;

namespace Chat.Api.Tests;

public class SlidingWindowLimiterTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    [Fact]
    public void It_allows_count_per_window_per_user()
    {
        var limiter = new SlidingWindowLimiter(() => (2, TimeSpan.FromSeconds(10)), _time);
        var user = Guid.NewGuid();

        Assert.True(limiter.TryAcquire(user));
        Assert.True(limiter.TryAcquire(user));
        Assert.False(limiter.TryAcquire(user));
        Assert.True(limiter.TryAcquire(Guid.NewGuid()));
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(limiter.TryAcquire(user));
    }

    [Fact]
    public void Users_who_stopped_are_forgotten_so_memory_does_not_grow_with_every_user_ever_seen()
    {
        var limiter = new SlidingWindowLimiter(() => (5, TimeSpan.FromSeconds(10)), _time);
        for (var i = 0; i < 3000; i++) limiter.TryAcquire(Guid.NewGuid());
        _time.Advance(TimeSpan.FromSeconds(11));

        var active = Guid.NewGuid();
        for (var i = 0; i < SlidingWindowLimiter.SweepEvery; i++)
        {
            limiter.TryAcquire(active);
            if (i % 5 == 4) _time.Advance(TimeSpan.FromSeconds(10)); // stay under the limit
        }

        Assert.True(limiter.TrackedUsers <= 1, $"still tracking {limiter.TrackedUsers} users");
    }
}

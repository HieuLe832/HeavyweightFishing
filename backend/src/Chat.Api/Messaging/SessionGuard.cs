using System.Collections.Concurrent;
using Chat.Api.Auth;
using Chat.Api.Realtime;
using Chat.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chat.Api.Messaging;

/// <summary>
/// Closes this node's hub connections whose session has ended. Commands re-check the session (<see cref="CommandValidator.CheckCallerAsync"/>),
/// but events go out by user id, so without this a socket outlives a logout on another device, a password reset or a ban and keeps
/// receiving every new message. Each node checks only its own connections, every <see cref="RealtimeOptions.SessionCheckSeconds"/>.
/// </summary>
public class SessionGuard(IServiceScopeFactory scopes, IOptions<RealtimeOptions> options, TimeProvider time, ILogger<SessionGuard> log)
    : BackgroundService
{
    private readonly ConcurrentDictionary<string, (Guid SessionId, HubCallerContext Context)> _connections = new();

    public void Add(HubCallerContext context) => _connections[context.ConnectionId] = (context.User!.SessionId(), context);

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    /// <summary>Aborts every connection whose session is revoked or expired, or whose user is no longer active. Returns how many.</summary>
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var held = _connections.Values.ToList();
        if (held.Count == 0) return 0;

        var sessionIds = held.Select(c => c.SessionId).Distinct().ToList();
        var now = time.GetUtcNow();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var live = (await db.Sessions
            .Where(s => sessionIds.Contains(s.Id) && s.RevokedAt == null && s.ExpiresAt > now
                && db.Users.Any(u => u.Id == s.UserId && u.Status == UserStatus.Active))
            .Select(s => s.Id).ToListAsync(ct)).ToHashSet();

        var ended = held.Where(c => !live.Contains(c.SessionId)).ToList();
        foreach (var (_, context) in ended) context.Abort();
        return ended.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.SessionCheckSeconds), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var closed = await SweepAsync(stoppingToken);
                if (closed > 0) log.LogInformation("Closed {Count} hub connections whose session ended", closed);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(e, "Could not check the sessions of open hub connections; trying again at the next tick");
            }
        }
    }
}

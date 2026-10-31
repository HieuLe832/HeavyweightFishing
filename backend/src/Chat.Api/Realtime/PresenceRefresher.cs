using Microsoft.Extensions.Options;

namespace Chat.Api.Realtime;

/// <summary>Renews this node's connection leases. A node that dies stops renewing, so its users go offline by themselves.</summary>
public class PresenceRefresher(PresenceService presence, IOptions<RealtimeOptions> options, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.RefreshSeconds), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await presence.RenewAsync(stoppingToken);
    }
}

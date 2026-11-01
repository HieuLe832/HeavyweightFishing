using System.Collections.Concurrent;
using System.Text.Json;

namespace Chat.Api.Realtime;

/// <summary>
/// The bus when RabbitMQ is not configured, and in tests. Handlers run inline; a failure is logged, as it would be a
/// dead letter with RabbitMQ. Everything published is kept so tests can look at it.
/// </summary>
public class InMemoryEventBus(IEnumerable<IBusHandler> handlers, ILogger<InMemoryEventBus> log) : IEventBus
{
    private readonly ConcurrentQueue<(string RoutingKey, JsonElement Body)> _published = new();

    public IReadOnlyCollection<(string RoutingKey, JsonElement Body)> Published => _published.ToArray();

    public async Task PublishAsync(string routingKey, object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, BusJson.Options);
        _published.Enqueue((routingKey, JsonSerializer.Deserialize<JsonElement>(bytes)));
        foreach (var handler in handlers.Where(h => h.RoutingKey == routingKey))
        {
            try
            {
                await handler.HandleAsync(bytes, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogError(e, "Handler {Handler} failed for {RoutingKey}", handler.Name, routingKey);
            }
        }
    }
}

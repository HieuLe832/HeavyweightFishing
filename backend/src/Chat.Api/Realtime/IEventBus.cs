namespace Chat.Api.Realtime;

/// <summary>
/// Durable events for work that must not be lost when nobody is listening (push). Live sockets do not use this: they get
/// their events through SignalR and the Redis backplane.
/// </summary>
public interface IEventBus
{
    /// <summary>Publishes <paramref name="payload"/> (as JSON) under <paramref name="routingKey"/>.</summary>
    Task PublishAsync(string routingKey, object payload, CancellationToken ct);
}

/// <summary>
/// A consumer of one routing key. With RabbitMQ it gets its own durable queue (<c>chat.{Name}</c>) and dead-letter queue;
/// a handler that throws sends the message to the dead-letter queue.
/// </summary>
public interface IBusHandler
{
    string Name { get; }
    string RoutingKey { get; }
    Task HandleAsync(ReadOnlyMemory<byte> body, CancellationToken ct);
}

/// <summary>Bus payloads are camelCase JSON, like the rest of the API.</summary>
public static class BusJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);
}

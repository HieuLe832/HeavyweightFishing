using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Chat.Api.Realtime;

/// <param name="Uri">An <c>amqp://</c> URI.</param>
public record RabbitMqSettings(string Uri);

public static class RabbitMqTopology
{
    public const string Exchange = "chat.events";
    public const string DeadExchange = "chat.events.dead";

    public static string Queue(IBusHandler handler) => $"chat.{handler.Name}";
    public static string DeadQueue(IBusHandler handler) => $"chat.{handler.Name}.dead";

    public static Task<IConnection> ConnectAsync(RabbitMqSettings settings, CancellationToken ct) =>
        new ConnectionFactory { Uri = new Uri(settings.Uri), AutomaticRecoveryEnabled = true }.CreateConnectionAsync(ct);

    public static async Task DeclareExchangesAsync(IChannel channel, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(DeadExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
    }
}

/// <summary>
/// Publishes to the durable topic exchange <c>chat.events</c> with persistent messages and publisher confirms, so a returned
/// call means the broker has the message. Connects on first use; after a failure the next publish connects again.
/// </summary>
public sealed class RabbitMqEventBus(RabbitMqSettings settings) : IEventBus, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1); // one publisher at a time: confirms are tracked per channel
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishAsync(string routingKey, object payload, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, BusJson.Options);
        await _gate.WaitAsync(ct);
        try
        {
            var channel = await ChannelAsync(ct);
            await channel.BasicPublishAsync(RabbitMqTopology.Exchange, routingKey, mandatory: false,
                new BasicProperties { Persistent = true, ContentType = "application/json" }, body, ct);
        }
        catch
        {
            await CloseAsync();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IChannel> ChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true }) return _channel;
        await CloseAsync();
        _connection = await RabbitMqTopology.ConnectAsync(settings, ct);
        _channel = await _connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        await RabbitMqTopology.DeclareExchangesAsync(_channel, ct);
        return _channel;
    }

    private async Task CloseAsync()
    {
        try
        {
            if (_connection is not null) await _connection.CloseAsync();
        }
        catch
        {
            // already gone
        }
        _channel = null;
        _connection = null;
    }

    public async ValueTask DisposeAsync() => await CloseAsync();
}

/// <summary>
/// Runs the <see cref="IBusHandler"/>s: each gets a durable queue bound to its routing key and a dead-letter queue.
/// A handler that throws sends its message to the dead-letter queue (no endless retry of a poison message).
/// Keeps trying to connect while RabbitMQ is down, so the API can start first.
/// </summary>
public class RabbitMqConsumer(RabbitMqSettings settings, IEnumerable<IBusHandler> handlers, ILogger<RabbitMqConsumer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var all = handlers.ToList();
        if (all.Count == 0) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await RabbitMqTopology.ConnectAsync(settings, stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await RabbitMqTopology.DeclareExchangesAsync(channel, stoppingToken);
                await channel.BasicQosAsync(0, 10, false, stoppingToken);
                foreach (var handler in all) await ConsumeAsync(channel, handler, stoppingToken);
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                log.LogWarning(e, "RabbitMQ consumer lost its connection; retrying in 5 seconds");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ConsumeAsync(IChannel channel, IBusHandler handler, CancellationToken ct)
    {
        var queue = RabbitMqTopology.Queue(handler);
        var dead = RabbitMqTopology.DeadQueue(handler);
        await channel.QueueDeclareAsync(dead, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(dead, RabbitMqTopology.DeadExchange, dead, cancellationToken: ct);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = RabbitMqTopology.DeadExchange,
            ["x-dead-letter-routing-key"] = dead,
        }, cancellationToken: ct);
        await channel.QueueBindAsync(queue, RabbitMqTopology.Exchange, handler.RoutingKey, cancellationToken: ct);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                await handler.HandleAsync(delivery.Body, ct);
                await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
            }
            // Only shutdown leaves the message unacknowledged (it is redelivered). Anything else, a TaskCanceledException from an
            // HttpClient timeout included, is a failure: unacknowledged, it would hold a prefetch slot until the consumer stalls.
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogError(e, "Handler {Handler} failed; dead-lettering the message", handler.Name);
                await channel.BasicNackAsync(delivery.DeliveryTag, false, requeue: false, ct);
            }
        };
        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, ct);
    }
}

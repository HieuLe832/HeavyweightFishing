using System.Text.Json;
using Chat.Api.Realtime;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;

namespace Chat.Api.Tests;

public class RabbitMqBusTests
{
    private sealed class TestHandler(string name, string key, Func<JsonElement, Task> onMessage) : IBusHandler
    {
        public string Name => name;
        public string RoutingKey => key;
        public Task HandleAsync(ReadOnlyMemory<byte> body, CancellationToken ct) => onMessage(JsonSerializer.Deserialize<JsonElement>(body.Span));
    }

    /// <summary>
    /// The topic exchange drops messages that have no queue yet, and the consumer binds its queue in the background, so
    /// publish until <paramref name="done"/> says it arrived. The bus is at-least-once anyway.
    /// </summary>
    private static async Task PublishUntilAsync(RabbitMqEventBus bus, string key, object payload, Func<Task<bool>> done)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!await done())
        {
            cts.Token.ThrowIfCancellationRequested();
            await bus.PublishAsync(key, payload, cts.Token);
            await Task.Delay(300, cts.Token);
        }
    }

    [Fact]
    public async Task A_published_event_reaches_the_handler_bound_to_its_routing_key()
    {
        var settings = new RabbitMqSettings(await SharedRabbitMq.UriAsync());
        var key = "test." + Guid.NewGuid().ToString("N");
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TestHandler("ok-" + Guid.NewGuid().ToString("N"), key, body => { received.TrySetResult(body); return Task.CompletedTask; });
        await using var bus = new RabbitMqEventBus(settings);
        using var consumer = new RabbitMqConsumer(settings, [handler], NullLogger<RabbitMqConsumer>.Instance);
        await consumer.StartAsync(CancellationToken.None);

        var id = Guid.NewGuid();
        await PublishUntilAsync(bus, key, new { MessageId = id, OfflineUserIds = new[] { Guid.NewGuid() } }, () => Task.FromResult(received.Task.IsCompleted));

        var body = await received.Task;
        Assert.Equal(id, body.GetProperty("messageId").GetGuid());
        await consumer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public Task A_handler_that_throws_sends_the_message_to_the_dead_letter_queue() =>
        AssertDeadLetteredAsync(() => new InvalidOperationException("poison"));

    /// <summary>An HttpClient timeout is a TaskCanceledException; it must not leave the message unacknowledged (which stalls the consumer).</summary>
    [Fact]
    public Task A_handler_that_times_out_sends_the_message_to_the_dead_letter_queue() =>
        AssertDeadLetteredAsync(() => new TaskCanceledException("timed out", new TimeoutException()));

    private static async Task AssertDeadLetteredAsync(Func<Exception> failure)
    {
        var settings = new RabbitMqSettings(await SharedRabbitMq.UriAsync());
        var key = "test." + Guid.NewGuid().ToString("N");
        var handler = new TestHandler("bad-" + Guid.NewGuid().ToString("N"), key, _ => throw failure());
        await using var bus = new RabbitMqEventBus(settings);
        using var consumer = new RabbitMqConsumer(settings, [handler], NullLogger<RabbitMqConsumer>.Instance);
        await consumer.StartAsync(CancellationToken.None);
        await using var connection = await RabbitMqTopology.ConnectAsync(settings, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            await bus.PublishAsync(key, new { Poison = true }, cts.Token);
            await Task.Delay(500, cts.Token);
            await using var probe = await connection.CreateChannelAsync(cancellationToken: cts.Token);
            try
            {
                if (await probe.MessageCountAsync(RabbitMqTopology.DeadQueue(handler), cts.Token) > 0) break;
            }
            catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
            {
                // the consumer has not declared the queue yet; a failed passive declare closes the probe channel, not the connection
            }
        }

        // the main queue is empty: the message was not retried forever
        await using var check = await connection.CreateChannelAsync();
        Assert.Equal(0u, await check.MessageCountAsync(RabbitMqTopology.Queue(handler)));
        await consumer.StopAsync(CancellationToken.None);
    }
}

using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace Chat.Api.Tests;

/// <summary>One Redis for the whole test run. Tests use unique ids, so they do not see each other's keys.</summary>
public static class SharedRedis
{
    private static readonly Lazy<Task<RedisContainer>> Container = new(async () =>
    {
        var container = new RedisBuilder("redis:7-alpine").Build();
        await container.StartAsync();
        return container;
    });

    public static async Task<string> ConnectionStringAsync() => (await Container.Value).GetConnectionString();
}

/// <summary>One RabbitMQ for the whole test run. Tests use their own handler names, so their queues do not clash.</summary>
public static class SharedRabbitMq
{
    private static readonly Lazy<Task<RabbitMqContainer>> Container = new(async () =>
    {
        var container = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();
        await container.StartAsync();
        return container;
    });

    public static async Task<string> UriAsync() => (await Container.Value).GetConnectionString();
}

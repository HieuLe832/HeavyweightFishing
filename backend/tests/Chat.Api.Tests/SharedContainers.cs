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

/// <summary>One S3-compatible object store (RustFS, the image Compose uses) for the whole test run.</summary>
public static class SharedObjectStore
{
    public const string AccessKey = "test-access", SecretKey = "test-secret-key";

    private static readonly Lazy<Task<DotNet.Testcontainers.Containers.IContainer>> Container = new(async () =>
    {
        var container = new DotNet.Testcontainers.Builders.ContainerBuilder("rustfs/rustfs:latest")
            .WithEnvironment("RUSTFS_ACCESS_KEY", AccessKey)
            .WithEnvironment("RUSTFS_SECRET_KEY", SecretKey)
            .WithPortBinding(9000, true)
            .WithWaitStrategy(DotNet.Testcontainers.Builders.Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9000).ForPath("/health")))
            .Build();
        await container.StartAsync();
        return container;
    });

    public static async Task<string> EndpointAsync()
    {
        var container = await Container.Value;
        return $"http://{container.Hostname}:{container.GetMappedPublicPort(9000)}";
    }
}

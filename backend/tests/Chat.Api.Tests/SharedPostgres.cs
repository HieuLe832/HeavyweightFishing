using Testcontainers.PostgreSql;

namespace Chat.Api.Tests;

/// <summary>One PostgreSQL container for the whole test run; each test class gets its own database in it.</summary>
public static class SharedPostgres
{
    private static readonly Lazy<Task<PostgreSqlContainer>> Container = new(async () =>
    {
        var container = new PostgreSqlBuilder("postgres:17-alpine").WithCommand("-c", "max_connections=1000").Build();
        await container.StartAsync();
        return container;
    });

    public static async Task<string> CreateDatabaseAsync()
    {
        var container = await Container.Value;
        var name = "t_" + Guid.NewGuid().ToString("N");
        await using (var admin = new Npgsql.NpgsqlConnection(container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE {name}";
            await cmd.ExecuteNonQueryAsync();
        }
        return new Npgsql.NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = name }
            .ConnectionString;
    }
}

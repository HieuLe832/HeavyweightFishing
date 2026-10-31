using Chat.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Chat.Data.Tests;

/// <summary>One real PostgreSQL with the migrations applied, shared by every test.</summary>
public class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = ChatDataExtensions.BuildChatDataSource(_container.GetConnectionString());
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    public ChatDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ChatDbContext>().ConfigureChat(DataSource).Options);

    public async Task<int> ExecAsync(string sql, params (string, object?)[] args)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<string>> QueryAsync(string sql)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows;
    }

    public async Task<Guid> InsertUserAsync(string? name = null)
    {
        name ??= "u" + Guid.NewGuid().ToString("N")[..12];
        var id = Guid.NewGuid();
        await ExecAsync(
            "INSERT INTO users (id, username, email, display_name, role, status) " +
            "VALUES (@id, @n, @e, @n, 'user', 'active')",
            ("id", id), ("n", name), ("e", name + "@example.com"));
        return id;
    }

    public async Task<Guid> InsertGroupAsync()
    {
        var id = Guid.NewGuid();
        await ExecAsync("INSERT INTO conversations (id, type) VALUES (@id, 'group')", ("id", id));
        return id;
    }
}

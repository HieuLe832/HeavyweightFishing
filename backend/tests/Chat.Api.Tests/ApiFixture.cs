using Chat.Api.Email;
using Chat.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Chat.Api.Tests;

/// <summary>The API hosted in-process on its own database, a fake clock and a capturing email sender.</summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private Factory _factory = null!;
    private string _connectionString = null!;

    public FakeTimeProvider Time { get; } = new(Start);
    public CapturingEmailSender Email { get; } = new();
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await SharedPostgres.CreateDatabaseAsync();
        await using (var dataSource = ChatDataExtensions.BuildChatDataSource(_connectionString))
        {
            var options = new DbContextOptionsBuilder<ChatDbContext>().ConfigureChat(dataSource).Options;
            await using var db = new ChatDbContext(options);
            await db.Database.MigrateAsync();
        }
        _factory = new Factory(this);
        Client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
    }

    public IServiceProvider Services => _factory.Services;

    public HttpClient NewClient() => _factory.CreateClient();

    public async Task WithDbAsync(Func<ChatDbContext, Task> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<ChatDbContext>());
    }

    public async Task<T> WithDbAsync<T>(Func<ChatDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ChatDbContext>());
    }

    /// <summary>A route guarded by the Admin policy, because no real admin endpoint exists yet.</summary>
    private sealed class AdminRouteFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(e => e.MapGet("/test/admin", () => "ok").RequireAuthorization("Admin"));
            next(app);
        };
    }

    private sealed class Factory(ApiFixture owner) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Chat", owner._connectionString);
            builder.UseSetting("Auth:JwtKey", "test-jwt-key-test-jwt-key-test-jwt-key-0123456789");
            builder.UseSetting("Auth:CodeKey", "test-code-key-0123456789");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(owner.Time);
                services.AddTransient<IStartupFilter, AdminRouteFilter>();
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(owner.Email);
            });
        }
    }
}

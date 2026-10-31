using Chat.Api.Email;
using Chat.Api.Push;
using Chat.Api.Realtime;
using Chat.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Chat.Api.Tests;

/// <summary>The API hosted in-process on its own database, a fake clock and a capturing email sender.</summary>
public class ApiFixture : IAsyncLifetime
{
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private Factory _factory = null!;
    private string _connectionString = null!;
    private readonly List<Factory> _nodes = [];

    /// <summary>Extra configuration for every node; a subclass adds Redis here.</summary>
    protected virtual Task<Dictionary<string, string?>> SettingsAsync() => Task.FromResult(new Dictionary<string, string?>());

    private Dictionary<string, string?> _settings = new();

    public FakeTimeProvider Time { get; } = new(Start);
    public CapturingEmailSender Email { get; } = new();
    public HttpClient Client { get; private set; } = null!;

    /// <summary>The presence store, which a test can make fail.</summary>
    public FaultablePresenceStore Presence { get; private set; } = null!;

    /// <summary>What went onto the event bus (RabbitMQ is not configured in tests).</summary>
    /// <summary>Every notification the (fake) push providers were asked to send.</summary>
    public RecordingPushSender Push { get; } = new();

    public InMemoryEventBus Bus => _factory.Services.GetRequiredService<InMemoryEventBus>();

    public async Task InitializeAsync()
    {
        _settings = await SettingsAsync();
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
        foreach (var node in _nodes) await node.DisposeAsync();
        await _factory.DisposeAsync();
    }

    /// <summary>
    /// Another API node on the same database and configuration (so, with Redis, the same backplane and presence).
    /// Connect to it with <see cref="Node.NewHub"/>.
    /// </summary>
    public Node AddNode()
    {
        var factory = new Factory(this);
        _nodes.Add(factory);
        factory.CreateClient().Dispose(); // starts the host
        return new Node(factory);
    }

    public sealed class Node(WebApplicationFactory<Program> factory)
    {
        public HttpClient NewClient() => factory.CreateClient();
        public HubConnection NewHub(string accessToken) => NewHubOn(factory, accessToken);
    }

    public IServiceProvider Services => _factory.Services;

    public HttpClient NewClient() => _factory.CreateClient();

    /// <summary>A not-yet-started connection to the chat hub, over the in-memory test server.</summary>
    public HubConnection NewHub(string accessToken) => NewHubOn(_factory, accessToken);

    private static HubConnection NewHubOn(WebApplicationFactory<Program> factory, string accessToken) => new HubConnectionBuilder()
        .WithUrl("http://localhost/hubs/chat", o =>
        {
            o.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            o.Transports = HttpTransportType.WebSockets;
            // Like a browser, send the token in the query string: the test WebSocket client sets no headers.
            o.WebSocketFactory = async (context, ct) => await factory.Server.CreateWebSocketClient()
                .ConnectAsync(new UriBuilder(context.Uri) { Query = $"{context.Uri.Query.TrimStart('?')}&access_token={Uri.EscapeDataString(accessToken)}" }.Uri, ct);
        })
        .Build();

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
            builder.UseSetting("Media:Cleanup:Enabled", "false"); // tests run the cleanup themselves, on their clock
            foreach (var (key, value) in owner._settings) builder.UseSetting(key, value);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(owner.Time);
                services.AddTransient<IStartupFilter, AdminRouteFilter>();
                services.RemoveAll<IPushSender>();
                services.AddSingleton<IPushSender>(owner.Push.For(Chat.Data.PushPlatform.Fcm));
                services.AddSingleton<IPushSender>(owner.Push.For(Chat.Data.PushPlatform.Apns));
                services.AddSingleton<IPushSender>(owner.Push.For(Chat.Data.PushPlatform.Web));
                if (!owner._settings.ContainsKey("Redis:ConnectionString"))
                {
                    services.RemoveAll<IPresenceStore>();
                    services.AddSingleton<IPresenceStore>(sp =>
                        owner.Presence = new FaultablePresenceStore(ActivatorUtilities.CreateInstance<InMemoryPresenceStore>(sp)));
                }
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(owner.Email);
            });
        }
    }
}

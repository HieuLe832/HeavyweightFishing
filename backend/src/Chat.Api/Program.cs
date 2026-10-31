using Chat.Api.Auth;
using Chat.Api.Conversations;
using Chat.Api.Email;
using Chat.Api.Files;
using Chat.Api.Messaging;
using Chat.Api.Moderation;
using Chat.Api.Push;
using Chat.Api.Realtime;
using Chat.Api.Users;
using Chat.Data;
using Chat.Data.Entities;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Chat")
    ?? throw new InvalidOperationException("ConnectionStrings:Chat is not configured.");
builder.Services.AddChatData(connectionString);

builder.Services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.Section)
    .Validate(AuthOptions.IsValid, "Auth:JwtKey must be at least 32 bytes and Auth:CodeKey at least 16 characters.")
    .ValidateOnStart();
builder.Services.AddOptions<SmtpOptions>().BindConfiguration(SmtpOptions.Section);
builder.Services.AddOptions<MessagingOptions>().BindConfiguration(MessagingOptions.Section);
builder.Services.AddOptions<RealtimeOptions>().BindConfiguration(RealtimeOptions.Section);

// Redis (the SignalR backplane and D9) and RabbitMQ (durable events) are optional: without them the API runs one node
// with in-memory presence and an in-process bus, which is what tests and a bare dev setup use.
var redis = builder.Configuration["Redis:ConnectionString"];
var rabbitMq = builder.Configuration["RabbitMq:Uri"];
var signalR = builder.Services.AddSignalR();
if (!string.IsNullOrWhiteSpace(redis))
{
    signalR.AddStackExchangeRedis(redis, o => o.Configuration.ChannelPrefix = RedisChannel.Literal("chat"));
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    {
        var options = ConfigurationOptions.Parse(redis);
        options.AbortOnConnectFail = false; // the API starts even if Redis is still coming up
        return ConnectionMultiplexer.Connect(options);
    });
    builder.Services.AddSingleton<IPresenceStore, RedisPresenceStore>();
}
else
{
    builder.Services.AddSingleton<IPresenceStore, InMemoryPresenceStore>();
}
if (!string.IsNullOrWhiteSpace(rabbitMq))
{
    builder.Services.AddSingleton(new RabbitMqSettings(rabbitMq));
    builder.Services.AddSingleton<IEventBus, RabbitMqEventBus>();
    builder.Services.AddHostedService<RabbitMqConsumer>();
}
else
{
    builder.Services.AddSingleton<InMemoryEventBus>();
    builder.Services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<InMemoryEventBus>());
}
builder.Services.AddPushSenders(builder.Configuration);
builder.Services.AddSingleton<IBusHandler, PushDispatcher>();
builder.Services.AddOptions<MediaOptions>().BindConfiguration(MediaOptions.Section);
var objectStore = builder.Configuration["ObjectStore:Endpoint"];
if (!string.IsNullOrWhiteSpace(objectStore))
    builder.Services.AddSingleton<IObjectStore>(new S3ObjectStore(new ObjectStoreSettings(
        objectStore, builder.Configuration["ObjectStore:AccessKey"] ?? "", builder.Configuration["ObjectStore:SecretKey"] ?? "",
        builder.Configuration["ObjectStore:Bucket"] ?? "chat-media")));
else
{
    builder.Services.AddSingleton<InMemoryObjectStore>();
    builder.Services.AddSingleton<IObjectStore>(sp => sp.GetRequiredService<InMemoryObjectStore>());
}
builder.Services.AddOptions<MediaCleanupOptions>().BindConfiguration(MediaCleanupOptions.Section);
builder.Services.AddHostedService<MediaCleanupService>();
builder.Services.AddSingleton<UploadRateLimiter>();
builder.Services.AddScoped<MediaService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddOptions<ModerationOptions>().BindConfiguration(ModerationOptions.Section);
builder.Services.AddSingleton<ReportRateLimiter>();
builder.Services.AddScoped<AuditWriter>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<ModerationService>();
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddSingleton<PresenceService>();
builder.Services.AddHostedService<PresenceRefresher>();
builder.Services.AddScoped<PresenceAudience>();
builder.Services.AddSingleton<IUserIdProvider, SubClaimUserIdProvider>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<CodeService>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<SignInService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.ConfigureOptions<JwtBearerSetup>();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Admin, p => p.RequireRole(TokenService.RoleName(UserRole.Admin)));
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<UserAccess>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<ContactService>();
builder.Services.AddScoped<ConversationReader>();
builder.Services.AddScoped<ConversationService>();
builder.Services.AddScoped<GroupService>();
builder.Services.AddSingleton<MessageRateLimiter>();
builder.Services.AddSingleton<TypingRateLimiter>();
builder.Services.AddScoped<ReceiptService>();
builder.Services.AddScoped<PresenceAnnouncer>();
builder.Services.AddScoped<CommandValidator>();
builder.Services.AddScoped<MessageWriter>();
builder.Services.AddScoped<MessageReader>();
builder.Services.AddScoped<MessageBroadcaster>();

var app = builder.Build();
PushRegistration.LogActiveSenders(app.Services.GetServices<IPushSender>(), app.Logger);

if (string.IsNullOrWhiteSpace(objectStore))
    app.Logger.LogWarning("ObjectStore:Endpoint is not configured: uploaded files are kept in memory and lost on restart.");
if (string.IsNullOrWhiteSpace(redis) || string.IsNullOrWhiteSpace(rabbitMq))
    app.Logger.LogWarning("Redis or RabbitMQ is not configured: running as a single node with in-memory presence and events.");

// Dev convenience: apply migrations on startup. Production applies them in the deploy step.
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<ChatDbContext>().Database.MigrateAsync();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", async (ChatDbContext db) =>
    await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503));
app.MapAuth();
app.MapUsers();
app.MapConversations();
app.MapHistory();
app.MapPresence();
app.MapPush();
app.MapMedia();
app.MapModeration();
app.MapHub<ChatHub>("/hubs/chat");

app.Run();

// Lets the integration tests host the app with WebApplicationFactory.
public partial class Program;

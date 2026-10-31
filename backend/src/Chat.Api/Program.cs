using Chat.Api.Auth;
using Chat.Api.Conversations;
using Chat.Api.Email;
using Chat.Api.Messaging;
using Chat.Api.Users;
using Chat.Data;
using Chat.Data.Entities;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Chat")
    ?? throw new InvalidOperationException("ConnectionStrings:Chat is not configured.");
builder.Services.AddChatData(connectionString);

builder.Services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.Section)
    .Validate(AuthOptions.IsValid, "Auth:JwtKey must be at least 32 bytes and Auth:CodeKey at least 16 characters.")
    .ValidateOnStart();
builder.Services.AddOptions<SmtpOptions>().BindConfiguration(SmtpOptions.Section);
builder.Services.AddOptions<MessagingOptions>().BindConfiguration(MessagingOptions.Section);
builder.Services.AddSignalR();
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
builder.Services.AddScoped<CommandValidator>();
builder.Services.AddScoped<MessageWriter>();
builder.Services.AddScoped<MessageReader>();
builder.Services.AddScoped<MessageBroadcaster>();

var app = builder.Build();

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
app.MapHub<ChatHub>("/hubs/chat");

app.Run();

// Lets the integration tests host the app with WebApplicationFactory.
public partial class Program;

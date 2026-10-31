using Chat.Api.Auth;
using Chat.Api.Email;
using Chat.Data;
using Chat.Data.Entities;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Chat")
    ?? throw new InvalidOperationException("ConnectionStrings:Chat is not configured.");
builder.Services.AddChatData(connectionString);

builder.Services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.Section)
    .Validate(AuthOptions.IsValid, "Auth:JwtKey must be at least 32 bytes and Auth:CodeKey at least 16 characters.")
    .ValidateOnStart();
builder.Services.AddOptions<SmtpOptions>().BindConfiguration(SmtpOptions.Section);
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

app.Run();

// Lets the integration tests host the app with WebApplicationFactory.
public partial class Program;

using Chat.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Chat")
    ?? throw new InvalidOperationException("ConnectionStrings:Chat is not configured.");
builder.Services.AddChatData(connectionString);

var app = builder.Build();

// Dev convenience: apply migrations on startup. Production applies them in the deploy step.
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<ChatDbContext>().Database.MigrateAsync();
}

app.MapGet("/health", async (ChatDbContext db) =>
    await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503));

app.Run();

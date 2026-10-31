using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Chat.Data;

/// <summary>Used by `dotnet ef`; it never connects, it only needs the provider and conventions.</summary>
internal class DesignTimeFactory : IDesignTimeDbContextFactory<ChatDbContext>
{
    public ChatDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("CHAT_DB")
                 ?? "Host=localhost;Port=5432;Database=chat;Username=chat;Password=chat";
        var options = new DbContextOptionsBuilder<ChatDbContext>()
            .ConfigureChat(ChatDataExtensions.BuildChatDataSource(cs));
        return new ChatDbContext(options.Options);
    }
}

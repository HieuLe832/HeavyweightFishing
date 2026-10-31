using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Chat.Data;

public static class ChatDataExtensions
{
    /// <summary>
    /// Enums are mapped on the data source as well as in the model; without both, inserts fail at runtime.
    /// </summary>
    public static NpgsqlDataSource BuildChatDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.MapEnum<UserRole>();
        builder.MapEnum<UserStatus>();
        builder.MapEnum<PushPlatform>();
        builder.MapEnum<VerificationPurpose>();
        builder.MapEnum<ConversationType>();
        builder.MapEnum<MemberRole>();
        builder.MapEnum<MessageType>();
        builder.MapEnum<MediaStatus>();
        builder.MapEnum<ReportReason>();
        builder.MapEnum<ReportStatus>();
        return builder.Build();
    }

    public static DbContextOptionsBuilder<ChatDbContext> ConfigureChat(
        this DbContextOptionsBuilder<ChatDbContext> options, NpgsqlDataSource dataSource)
    {
        ConfigureChatCore(options, dataSource);
        return options;
    }

    // EF Core needs the enums mapped on its own options builder too (this also registers them in the model).
    private static void ConfigureChatCore(DbContextOptionsBuilder options, NpgsqlDataSource dataSource) =>
        options.UseNpgsql(dataSource, npgsql => npgsql
                .MapEnum<UserRole>()
                .MapEnum<UserStatus>()
                .MapEnum<PushPlatform>()
                .MapEnum<VerificationPurpose>()
                .MapEnum<ConversationType>()
                .MapEnum<MemberRole>()
                .MapEnum<MessageType>()
                .MapEnum<MediaStatus>()
                .MapEnum<ReportReason>()
                .MapEnum<ReportStatus>())
            .UseSnakeCaseNamingConvention();

    public static IServiceCollection AddChatData(this IServiceCollection services, string connectionString)
    {
        var dataSource = BuildChatDataSource(connectionString);
        services.AddSingleton(dataSource);
        services.AddDbContext<ChatDbContext>(o => ConfigureChatCore(o, dataSource));
        return services;
    }
}

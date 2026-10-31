using Chat.Data;

namespace Chat.Api.Push;

public static class PushRegistration
{
    /// <summary>
    /// Registers one <see cref="IPushSender"/> per platform: the real one when its <c>Push</c> section is complete, the logging
    /// one when it is empty. A half-filled section throws, so a typo in a key name cannot silently turn pushes off.
    /// </summary>
    public static IServiceCollection AddPushSenders(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(PushOptions.Section).Get<PushOptions>() ?? new PushOptions();
        AddFcm(services, options.Fcm);
        AddApns(services, options.Apns);
        AddWeb(services, options.Web);
        return services;
    }

    /// <summary>Says at start which platforms really send and which only log.</summary>
    public static void LogActiveSenders(IEnumerable<IPushSender> senders, ILogger log)
    {
        foreach (var sender in senders.OrderBy(s => s.Platform))
        {
            if (sender is LoggingPushSender)
                log.LogWarning("Push ({Platform}): no provider configured, notifications are logged, not sent", sender.Platform);
            else
                log.LogInformation("Push ({Platform}): sending through {Sender}", sender.Platform, sender.GetType().Name);
        }
    }

    private static void AddFcm(IServiceCollection services, FcmOptions options)
    {
        if (!options.IsEmpty && !File.Exists(options.ServiceAccountJsonPath))
            throw new InvalidOperationException($"Push:Fcm:ServiceAccountJsonPath points at a file that does not exist: {options.ServiceAccountJsonPath}");
        AddLogging(services, PushPlatform.Fcm);
    }

    private static void AddApns(IServiceCollection services, ApnsOptions options)
    {
        if (!options.IsEmpty && options.Missing() is { Count: > 0 } missing)
            throw new InvalidOperationException($"Push:Apns is partly configured; missing {string.Join(", ", missing)}.");
        if (options.IsEmpty)
        {
            AddLogging(services, PushPlatform.Apns);
            return;
        }
        if (!File.Exists(options.KeyPath))
            throw new InvalidOperationException($"Push:Apns:KeyPath points at a file that does not exist: {options.KeyPath}");
        services.AddHttpClient("apns");
        services.AddSingleton<IPushSender>(sp => new ApnsPushSender(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("apns"), options, sp.GetRequiredService<TimeProvider>()));
    }

    private static void AddWeb(IServiceCollection services, WebOptions options)
    {
        if (!options.IsEmpty && options.Missing() is { Count: > 0 } missing)
            throw new InvalidOperationException($"Push:Web is partly configured; missing {string.Join(", ", missing)}.");
        AddLogging(services, PushPlatform.Web);
    }

    private static void AddLogging(IServiceCollection services, PushPlatform platform) =>
        services.AddSingleton<IPushSender>(sp => new LoggingPushSender(platform, sp.GetRequiredService<ILogger<LoggingPushSender>>()));
}

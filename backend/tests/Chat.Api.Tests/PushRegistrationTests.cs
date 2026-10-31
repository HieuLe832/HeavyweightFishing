using Chat.Api.Push;
using Chat.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chat.Api.Tests;

public class PushRegistrationTests
{
    internal static IServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddPushSenders(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void With_no_push_settings_all_three_platforms_use_the_logging_sender()
    {
        var senders = Build([]).GetServices<IPushSender>().ToList();

        Assert.All(senders, s => Assert.IsType<LoggingPushSender>(s));
        Assert.Equal([PushPlatform.Fcm, PushPlatform.Apns, PushPlatform.Web], senders.Select(s => s.Platform).Order());
    }

    [Fact]
    public void A_half_filled_apns_section_throws_naming_the_missing_keys()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Build(new() { ["Push:Apns:KeyId"] = "K1", ["Push:Apns:TeamId"] = "T1" }));

        Assert.Contains("Push:Apns:KeyPath", e.Message);
        Assert.Contains("Push:Apns:BundleId", e.Message);
        Assert.DoesNotContain("Push:Apns:KeyId", e.Message);
    }

    [Fact]
    public void A_half_filled_web_section_throws_naming_the_missing_keys()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Build(new() { ["Push:Web:PublicKey"] = "pub" }));

        Assert.Contains("Push:Web:PrivateKey", e.Message);
        Assert.Contains("Push:Web:Subject", e.Message);
    }

    [Fact]
    public void A_missing_fcm_file_throws_naming_the_path()
    {
        var path = Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".json");

        var e = Assert.Throws<InvalidOperationException>(() => Build(new() { ["Push:Fcm:ServiceAccountJsonPath"] = path }));

        Assert.Contains(path, e.Message);
    }

    [Fact]
    public void Empty_strings_count_as_unset()
    {
        var senders = Build(new() { ["Push:Apns:KeyId"] = "", ["Push:Web:PublicKey"] = " ", ["Push:Fcm:ServiceAccountJsonPath"] = "" })
            .GetServices<IPushSender>().ToList();

        Assert.All(senders, s => Assert.IsType<LoggingPushSender>(s));
    }
}
